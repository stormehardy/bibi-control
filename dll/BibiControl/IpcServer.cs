using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace BibiControl;

// A small newline-delimited-JSON request/response server compatible with the Go
// ipc package. The control plane dials in; each request is dispatched to a
// registered command handler that runs on the Unity main thread (so handlers may
// touch game state directly), and the typed result is returned as a response.
//
// Extensibility: call Register(name, handler) for each command. Network/threading
// is generic here; game-specific behaviour lives entirely in the handlers
// (see SimCommands).
public sealed class IpcServer : MonoBehaviour
{
	// A handler receives the request payload (may be null) and returns an object
	// to be serialized as the response payload. Throwing produces an error reply.
	public delegate object CommandHandler(JToken payload);

	private string _host = "127.0.0.1";
	private int _port = 43100;

	private TcpListener _listener;
	private Thread _acceptThread;
	private volatile bool _running;

	private readonly Dictionary<string, CommandHandler> _handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
	private readonly Dictionary<string, CommandHandler> _asyncHandlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
	private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
	private readonly List<TcpClient> _clients = new List<TcpClient>();
	private readonly object _clientsLock = new object();

	private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
	{
		// Mirror Go's `omitempty` for nil fields so the envelope stays minimal.
		NullValueHandling = NullValueHandling.Ignore,
	};

	public void Configure(string host, int port)
	{
		_host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
		_port = port;
	}

	public void Register(string command, CommandHandler handler)
	{
		_handlers[command] = handler;
	}

	// Registers a long-running handler. Unlike Register, an async handler runs on
	// the network thread (it is NOT marshaled through RunOnMain), so it may take
	// many seconds and drive a multi-frame coroutine on the main thread via
	// StartCoroutineOnMain without blocking the frame loop. The handler is
	// responsible for its own main-thread marshaling.
	public void RegisterAsync(string command, CommandHandler handler)
	{
		_asyncHandlers[command] = handler;
	}

	private void Start()
	{
		try
		{
			IPAddress addr = _host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_host);
			_listener = new TcpListener(addr, _port);
			_listener.Start();
			_running = true;
			_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BibiControlIpcAccept" };
			_acceptThread.Start();
			Debug.Log("[BibiControl] IPC listening on " + _host + ":" + _port);
		}
		catch (Exception e)
		{
			Debug.LogError("[BibiControl] failed to start IPC listener: " + e);
		}
	}

	// Drain main-thread jobs. Update runs every frame regardless of Time.timeScale,
	// so commands keep working even while the simulation is paused (time scale 0).
	private void Update()
	{
		Action action;
		while (_mainThreadQueue.TryDequeue(out action))
		{
			try { action(); }
			catch (Exception e) { Debug.LogError("[BibiControl] main-thread job failed: " + e); }
		}
	}

	private void OnDestroy() => Shutdown();

	private void OnApplicationQuit() => Shutdown();

	private void Shutdown()
	{
		_running = false;
		try { _listener?.Stop(); } catch { }
		lock (_clientsLock)
		{
			foreach (TcpClient c in _clients)
			{
				try { c.Close(); } catch { }
			}
			_clients.Clear();
		}
	}

	private void AcceptLoop()
	{
		while (_running)
		{
			TcpClient client;
			try { client = _listener.AcceptTcpClient(); }
			catch { break; }
			lock (_clientsLock) { _clients.Add(client); }
			Thread t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "BibiControlIpcClient" };
			t.Start();
		}
	}

	private void HandleClient(TcpClient client)
	{
		try
		{
			using (NetworkStream stream = client.GetStream())
			using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
			using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false })
			{
				string line;
				while (_running && (line = reader.ReadLine()) != null)
				{
					if (line.Length == 0) continue;
					Envelope reply = ProcessLine(line);
					if (reply == null) continue;
					writer.Write(JsonConvert.SerializeObject(reply, JsonSettings));
					writer.Write('\n');
					writer.Flush();
				}
			}
		}
		catch (Exception e)
		{
			Debug.Log("[BibiControl] client connection closed: " + e.Message);
		}
		finally
		{
			lock (_clientsLock) { _clients.Remove(client); }
			try { client.Close(); } catch { }
		}
	}

	private Envelope ProcessLine(string line)
	{
		Envelope req;
		try { req = JsonConvert.DeserializeObject<Envelope>(line); }
		catch (Exception e)
		{
			Debug.LogWarning("[BibiControl] dropping malformed envelope: " + e.Message);
			return null;
		}
		if (req == null) return null;
		// Only answer requests; ignore stray responses/events for now.
		if (req.Kind != "request") return null;

		Envelope reply = new Envelope
		{
			Id = Guid.NewGuid().ToString("N"),
			ReplyTo = req.Id,
			Kind = "response",
			Time = NowRfc3339(),
		};

		// Async commands run on this (network) thread and do their own marshaling;
		// they may block for many seconds (e.g. minting a save) without stalling
		// the main thread. Checked before the sync registry.
		CommandHandler asyncHandler;
		if (_asyncHandlers.TryGetValue(req.Command ?? string.Empty, out asyncHandler))
		{
			try
			{
				object result = asyncHandler(req.Payload);
				reply.Payload = result as JToken ?? (result != null ? JToken.FromObject(result) : null);
			}
			catch (Exception e)
			{
				reply.Kind = "error";
				reply.Error = e.Message;
			}
			return reply;
		}

		CommandHandler handler;
		if (!_handlers.TryGetValue(req.Command ?? string.Empty, out handler))
		{
			reply.Kind = "error";
			reply.Error = "unknown command: " + req.Command;
			return reply;
		}

		try
		{
			object result = RunOnMain(() => handler(req.Payload));
			// Handlers return a JToken (JObject) today; FromObject also supports
			// returning a plain POCO from a future handler.
			reply.Payload = result as JToken ?? (result != null ? JToken.FromObject(result) : null);
		}
		catch (Exception e)
		{
			reply.Kind = "error";
			reply.Error = e.Message;
		}
		return reply;
	}

	// Runs fn on the Unity main thread and blocks the caller until it completes,
	// rethrowing any exception. Used by network threads to safely touch game state.
	public T RunOnMain<T>(Func<T> fn, int timeoutMs = 15000)
	{
		T result = default(T);
		Exception error = null;
		ManualResetEventSlim done = new ManualResetEventSlim(false);
		_mainThreadQueue.Enqueue(() =>
		{
			try { result = fn(); }
			catch (Exception e) { error = e; }
			finally { done.Set(); }
		});
		if (!done.Wait(timeoutMs))
			throw new TimeoutException("main-thread dispatch timed out");
		if (error != null) throw error;
		return result;
	}

	// Starts a coroutine on this component from any thread (the StartCoroutine call
	// itself is marshaled onto the main thread). Because this component is
	// DontDestroyOnLoad the coroutine survives scene loads. Fire-and-forget: the
	// coroutine signals its own completion (e.g. via a result object the caller
	// polls), so use this for long operations instead of the blocking RunOnMain.
	public void StartCoroutineOnMain(IEnumerator routine)
	{
		_mainThreadQueue.Enqueue(() => StartCoroutine(routine));
	}

	private static string NowRfc3339() =>
		DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
