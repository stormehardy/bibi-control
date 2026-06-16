using System;
using System.Collections;
using ManagementScripts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BibiControl;

// Headless-mode entry point and IPC bootstrap.
//
// Because this is compiled into BibitesAssembly, Unity invokes Bootstrap()
// automatically at startup via [RuntimeInitializeOnLoadMethod] — no external
// loader (BepInEx/Doorstop) is required, so it works on every platform the
// managed assembly runs on (incl. macOS/ARM).
//
// Command-line flags (pass after the executable; combine with Unity's own
// -batchmode -nographics for a true headless run):
//   -bibiteHeadless            enable headless mode and start the IPC server
//   -bibiteSave <path|latest>  save to auto-load on start ("latest" = newest)
//   -bibiteIpcPort <port>      TCP listen port for the IPC server (default 43100)
//   -bibiteIpcHost <host>      bind host (default 127.0.0.1; use 0.0.0.0 for any)
//
// Without -bibiteSave the server still starts but no world is auto-loaded; a
// RELOAD command can load the most recent save later.
public static class HeadlessController
{
	public static bool Enabled { get; private set; }
	public static string SaveArg { get; private set; }
	public static int Port { get; private set; } = 43100;
	public static string Host { get; private set; } = "127.0.0.1";

	public static bool MintEnabled { get; private set; }
	public static string MintScenario { get; private set; }
	public static string MintOut { get; private set; }

	private static bool _parsed;
	private static bool _bootstrapped;
	private static bool _launched;
	private static IpcServer _server;
	private static MintRunner _mintRunner;
	private static bool _mintStarted;

	// Entry point. Reached either via Unity's [RuntimeInitializeOnLoadMethod] (when
	// the mod is part of an auto-scanned assembly) or via a call injected into
	// AppInitializer.Awake (the IL-patch loader). Idempotent: safe to call twice.
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	public static void Bootstrap()
	{
		if (_bootstrapped)
			return;
		_bootstrapped = true;

		ParseArgs();
		// Single load marker so you can confirm in Player.log that the mod is
		// actually loaded and this startup hook ran.
		Debug.Log("[BibiControl] mod loaded (headless=" + Enabled + ", mint=" + MintEnabled + ")");
		if (MintEnabled)
		{
			// One-shot: mint a seed save from a scenario, then quit. Independent of
			// the IPC server and of -bibiteHeadless / -bibiteSave.
			StartMintMode();
			return;
		}
		if (!Enabled)
			return;
		StartServer();
		// AppInitializer.Start() opens the menu. Once the menu scene has loaded,
		// redirect into the simulation with the requested save instead of idling
		// in the menu. Gating on activeScene == Menu (rather than a scene name)
		// keeps this robust regardless of the bootstrap scene's name.
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	public static void ParseArgs()
	{
		if (_parsed)
			return;
		_parsed = true;

		string[] args = Environment.GetCommandLineArgs();
		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "-bibiteHeadless":
					Enabled = true;
					break;
				case "-bibiteSave":
					if (i + 1 < args.Length)
						SaveArg = args[++i];
					break;
				case "-bibiteIpcPort":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
					{
						Port = port;
						i++;
					}
					break;
				case "-bibiteIpcHost":
					if (i + 1 < args.Length)
						Host = args[++i];
					break;
				case "-bibiteMintSeed":
					MintEnabled = true;
					break;
				case "-bibiteScenario":
					if (i + 1 < args.Length)
						MintScenario = args[++i];
					break;
				case "-bibiteOut":
					if (i + 1 < args.Length)
						MintOut = args[++i];
					break;
			}
		}
	}

	private static void StartServer()
	{
		if (_server != null)
			return;
		GameObject go = new GameObject("BibiControlIpcServer");
		UnityEngine.Object.DontDestroyOnLoad(go);
		_server = go.AddComponent<IpcServer>();
		_server.Configure(Host, Port);
		SimCommands.Register(_server);
		Debug.Log("[BibiControl] headless enabled; IPC server " + Host + ":" + Port);
	}

	// One-shot seed-mint mode: wait for the menu (so the game has initialized and
	// imported its bundled scenarios), drive the mint, then quit. The mint itself
	// navigates into the simulation scene.
	private static void StartMintMode()
	{
		if (string.IsNullOrEmpty(MintOut))
		{
			Debug.LogError("[BibiControl] -bibiteMintSeed requires -bibiteOut <path>");
			Application.Quit(2);
			return;
		}
		GameObject go = new GameObject("BibiControlMintRunner");
		UnityEngine.Object.DontDestroyOnLoad(go);
		_mintRunner = go.AddComponent<MintRunner>();
		_mintRunner.Begin(MintScenario, MintOut);
		Debug.Log("[BibiControl] mint mode: scenario=" + (MintScenario ?? "default") + " out=" + MintOut);
		SceneManager.sceneLoaded += OnMintSceneLoaded;
	}

	private static void OnMintSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (_mintStarted)
			return;
		if (GameManager.activeScene != BibiteScenes.Menu)
			return;
		_mintStarted = true;
		SceneManager.sceneLoaded -= OnMintSceneLoaded;
		_mintRunner.RunOnce();
	}

	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (!Enabled || _launched)
			return;
		// Only act once the game has navigated to the menu (post-initialization).
		if (GameManager.activeScene != BibiteScenes.Menu)
			return;
		_launched = true;

		string save = ResolveSave();
		if (string.IsNullOrEmpty(save))
		{
			Debug.LogWarning("[BibiControl] headless: no save to load (use -bibiteSave or RELOAD).");
			return;
		}
		// Wait for the procedural sprite atlas before entering the sim, then load.
		// Without this, loaded pellets / HUD previewers index a not-yet-populated
		// sprite list and spam IndexOutOfRange (non-fatal, but noisy). Driven on the
		// IPC server (DontDestroyOnLoad, so the coroutine survives the scene load).
		if (_server != null)
			_server.StartCoroutine(LoadAfterSprites(save));
		else
		{
			Debug.Log("[BibiControl] headless: loading save " + save);
			GameManager.StartGame(save);
		}
	}

	private static IEnumerator LoadAfterSprites(string save)
	{
		yield return SeedMinter.WaitForSprites();
		Debug.Log("[BibiControl] headless: loading save " + save);
		GameManager.StartGame(save);
	}

	// Resolves -bibiteSave into a concrete path. "latest" picks the newest save
	// (manual or autosave); returns null when nothing is available.
	public static string ResolveSave()
	{
		if (string.IsNullOrEmpty(SaveArg))
			return null;
		if (string.Equals(SaveArg, "latest", StringComparison.OrdinalIgnoreCase))
		{
			try { return SaveController.AnySave() ? SaveController.GetLastSave() : null; }
			catch { return null; }
		}
		return SaveArg;
	}
}
