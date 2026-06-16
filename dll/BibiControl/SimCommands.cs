using System;
using System.IO;
using System.Threading;
using ManagementScripts;
using Newtonsoft.Json.Linq;
using OneUseScripts;
using SettingScripts;
using UnityEngine;

namespace BibiControl;

// The STOP / RESUME / INFO / RELOAD command handlers.
//
// Every handler here runs on the Unity main thread (IpcServer marshals it), so
// it may read and mutate game state directly. The JSON field names must match
// the Go ipc package (ipc/commands.go) and the simctl client.
//
// To add a command: write a handler, register it below, and mirror the
// payload/result on the Go side (a constant + types in ipc, a method in simctl).
public static class SimCommands
{
	private static TimeKeeper _timeKeeper;
	private static IpcServer _server;

	private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	public static void Register(IpcServer server)
	{
		_server = server;
		server.Register("STOP", Stop);
		server.Register("RESUME", Resume);
		server.Register("INFO", Info);
		server.Register("RELOAD", Reload);
		server.RegisterAsync("NEWSAVE", NewSave);
	}

	// STOP: pause by forcing the engine time scale to 0. Returns the configured
	// (target) speed so the caller can restore it later via RESUME.
	private static object Stop(JToken payload)
	{
		RequireSimulation();
		float previous = TimeController.targetTimeScale.val;
		TimeController.engineTimeScale.SetValue(0f);
		return new JObject { ["previous_time_scale"] = previous };
	}

	// RESUME: run at the requested time scale. Sets both the target (so the UI /
	// min-FPS ceiling agree) and the engine scale (so the speed takes effect now).
	private static object Resume(JToken payload)
	{
		RequireSimulation();
		float scale = 1f;
		if (payload != null && payload["time_scale"] != null)
			scale = payload["time_scale"].Value<float>();
		if (scale <= 0f)
			throw new ArgumentException("time_scale must be > 0");
		TimeController.targetTimeScale.SetValue(scale);
		TimeController.engineTimeScale.SetValue(scale);
		return new JObject { ["time_scale"] = scale };
	}

	// INFO: live telemetry. Intentionally returns a superset of the documented
	// fields; new fields can be added without breaking older clients.
	private static object Info(JToken payload)
	{
		JObject obj = new JObject
		{
			["tps"] = ScenarioIndependentSettings.Instance.simTPS.val,
			["real_tps"] = RealTps(),
			["paused"] = TimeController.paused,
			["sim_time"] = TimeKeeper.simulatedTime,
		};
		JObject autosave = LastAutosave();
		if (autosave != null)
			obj["last_autosave"] = autosave;
		return obj;
	}

	// RELOAD: reload the most recent save (manual saves and autosaves combined).
	// Triggers a scene reload; the IPC server survives it (DontDestroyOnLoad).
	private static object Reload(JToken payload)
	{
		if (!SaveController.AnySave())
			throw new InvalidOperationException("no save to reload");
		string save = SaveController.GetLastSave();
		GameManager.StartGame(save);
		return new JObject { ["save"] = save, ["ok"] = true };
	}

	// NEWSAVE: mint a fresh, engine-made save from a scenario without the GUI —
	// load the scenario, start a new game, let the world initialize, then save.
	// Registered async: it runs on a network thread, drives the mint coroutine on
	// the main thread, and blocks only this thread until the save is written (so
	// it is exempt from RunOnMain's short timeout). Payload:
	//   { "scenario": "<abs .zip | 'default' | omitted>", "out": "<abs .zip>" }
	private static object NewSave(JToken payload)
	{
		string outPath = payload != null && payload["out"] != null ? payload["out"].Value<string>() : null;
		if (string.IsNullOrEmpty(outPath))
			throw new ArgumentException("out (destination .zip path) is required");
		string scenario = payload != null && payload["scenario"] != null ? payload["scenario"].Value<string>() : null;

		SeedMinter.MintResult res = new SeedMinter.MintResult();
		_server.StartCoroutineOnMain(SeedMinter.MintSeedSave(scenario, outPath, res));

		System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
		while (!res.Done)
		{
			if (sw.ElapsedMilliseconds > 180000)
				throw new TimeoutException("mint did not complete within 180s");
			Thread.Sleep(20);
		}
		if (res.Error != null)
			throw new InvalidOperationException("mint failed: " + res.Error.Message);
		return new JObject { ["path"] = res.Path, ["ok"] = true };
	}

	private static void RequireSimulation()
	{
		// TimeController.Instance is a UnityEngine.Object; == null also catches a
		// destroyed instance (e.g. between scene reloads).
		if (TimeController.Instance == null)
			throw new InvalidOperationException("simulation not running");
	}

	private static float RealTps()
	{
		if (_timeKeeper == null)
			_timeKeeper = UnityEngine.Object.FindFirstObjectByType<TimeKeeper>();
		return _timeKeeper != null ? _timeKeeper.logicFramePerSeconds : 0f;
	}

	private static JObject LastAutosave()
	{
		string dir = SaveController.AutoSavePath;
		if (!Directory.Exists(dir))
			return null;

		string newest = null;
		DateTime mtime = DateTime.MinValue;
		foreach (string f in Directory.GetFiles(dir, "*.zip"))
		{
			DateTime w = File.GetLastWriteTimeUtc(f);
			if (newest == null || w > mtime)
			{
				newest = f;
				mtime = w;
			}
		}
		if (newest == null)
			return null;

		return new JObject
		{
			["path"] = newest,
			["name"] = Path.GetFileName(newest),
			["modified_unix"] = (long)(mtime - UnixEpoch).TotalSeconds,
			["time"] = mtime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
		};
	}
}
