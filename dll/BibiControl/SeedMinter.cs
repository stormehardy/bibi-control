using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using ManagementScripts;
using Newtonsoft.Json.Linq;
using ScriptHelpers;
using SettingScripts;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace BibiControl;

// Core "new game from a scenario -> save" minter.
//
// Produces a fresh, engine-made save (.zip) from a scenario *without any GUI*:
// load the scenario into ScenarioSettings.Instance, start a new game, let the
// world initialize, then run the game's own save coroutine. The result is a
// self-contained, loadable save — the same artifact the game would write if a
// player picked the scenario and hit Save. (A scenario is a strict subset of a
// save, so this is the faithful way to seed one programmatically.)
//
// The mint is inherently multi-frame (a scene load + Unity's save coroutine), so
// it is exposed as a coroutine. Host it on a DontDestroyOnLoad MonoBehaviour so
// it survives the scene change StartGame triggers (the IPC server, or MintRunner
// below). Both the one-shot launch mode and the NEWSAVE IPC command use this.
public static class SeedMinter
{
	// Completion handoff between the frame-driven coroutine and whatever waits on
	// it (a coroutine host, or a polling network thread). Writers set Path/Error
	// before Done; readers check Done first (the volatile write of Done publishes
	// the earlier writes).
	public sealed class MintResult
	{
		public volatile bool Done;
		public volatile string Path;
		public volatile Exception Error;
	}

	// scenarioPathOrNull: absolute path to a scenario .zip (.bb8scenario), or null
	//   / "default" to use the bundled Default scenario the game imports at boot.
	// outPath: absolute destination .zip for the minted save (written verbatim).
	public static IEnumerator MintSeedSave(string scenarioPathOrNull, string outPath, MintResult res)
	{
		// --- 1. Load the scenario into ScenarioSettings.Instance (pure data). ---
		Exception err = null;
		try
		{
			LoadScenario(ResolveScenario(scenarioPathOrNull));
			// Near-empty seed: disable initial pellet seeding. This also keeps the
			// new-game init off the procedural-sprite path (sprites aren't ready the
			// instant we enter the sim), which otherwise throws IndexOutOfRange.
			ScenarioSettings.Instance.initialSeeding.SetValue(0f);
			// Empty, purely-additive seed: clear the spawn list so no bibites spawn.
			ScenarioSettings.Instance.bibites.Clear();
		}
		catch (Exception e) { err = e; }
		if (err != null) { Fail(res, err); yield break; }
		yield return null;

		// Wait for the procedural sprite atlas before entering the sim.
		yield return WaitForSprites();

		// --- 2. Start a NEW game (no save) and wait for the world to initialize. ---
		bool simReady = false;
		UnityAction onBeforeSim = delegate { simReady = true; };
		UnityAction<Scene, LoadSceneMode> onLoaded = null;
		onLoaded = delegate (Scene scene, LoadSceneMode mode)
		{
			if (GameManager.activeScene != BibiteScenes.Simulation)
				return;
			SceneManager.sceneLoaded -= onLoaded;
			// beforeSimStart fires from SimulationManager.Start, after the spawners
			// are built — the moment the world is coherent enough to serialize.
			if (SimulationManager.Instance != null)
				SimulationManager.Instance.beforeSimStart.AddListener(onBeforeSim);
			else
				simReady = true;
		};
		SceneManager.sceneLoaded += onLoaded;

		try
		{
			GameManager.saveToLoad = "";   // force SimulationManager's new-game branch
			GameManager.StartGame();        // loads the "Main" scene
		}
		catch (Exception e) { err = e; }
		if (err != null)
		{
			SceneManager.sceneLoaded -= onLoaded;
			Fail(res, err);
			yield break;
		}

		float t0 = Time.realtimeSinceStartup;
		while (!simReady)
		{
			if (Time.realtimeSinceStartup - t0 > 60f)
			{
				SceneManager.sceneLoaded -= onLoaded;
				Fail(res, new TimeoutException("world did not initialize within 60s"));
				yield break;
			}
			yield return null;
		}

		// Freeze the sim so nothing spawns (no time-based pellet/bibite production)
		// during the settle + save window — keeps the seed clean and additive.
		TimeController.engineTimeScale.SetValue(0f);

		// Let every scene Start() run (SaveSystem builds its save stack in Start)
		// before serializing.
		for (int i = 0; i < 3; i++)
			yield return null;

		// --- 3. Emit the save to outPath and wait for completion. ---
		if (SaveSystem.instance == null)
		{
			Fail(res, new InvalidOperationException("SaveSystem missing after entering the simulation"));
			yield break;
		}

		bool savingDone = false;
		UnityAction onSaved = delegate { savingDone = true; };
		SaveSystem.instance.onSavingDone.AddListener(onSaved);

		try
		{
			EnsureParentDir(outPath);
			SaveSystem.instance.SaveGame(outPath);   // writes the zip at outPath exactly
		}
		catch (Exception e) { err = e; }
		if (err != null)
		{
			SaveSystem.instance.onSavingDone.RemoveListener(onSaved);
			Fail(res, err);
			yield break;
		}

		float t1 = Time.realtimeSinceStartup;
		while (!savingDone)
		{
			if (Time.realtimeSinceStartup - t1 > 60f)
			{
				SaveSystem.instance.onSavingDone.RemoveListener(onSaved);
				Fail(res, new TimeoutException("save did not complete within 60s"));
				yield break;
			}
			yield return null;
		}
		SaveSystem.instance.onSavingDone.RemoveListener(onSaved);

		res.Path = outPath;
		res.Done = true;
	}

	// Waits (best effort, up to 90s) for the procedural sprite atlas to finish
	// loading. The menu (MenuInitializer) kicks this async load off and the manager
	// persists into the sim scene; pellets and HUD previewers index these sprite
	// lists, so entering the sim before they're populated throws IndexOutOfRange.
	// Shared by the mint path and the headless save-load path.
	public static IEnumerator WaitForSprites()
	{
		ProceduralSpriteManager psm = ProceduralSpriteManager.Instance;
		float t0 = Time.realtimeSinceStartup;
		while (psm != null && !psm.done)
		{
			if (Time.realtimeSinceStartup - t0 > 90f)
				break;   // best effort: proceed even if it never reports done
			yield return null;
			psm = ProceduralSpriteManager.Instance;
		}
	}

	// Resolves the scenario to load. An explicit path is used as-is; null/"default"
	// resolves to the game's bundled Default scenario (written to disk at boot by
	// AppInitializer.ReImportOfficialScenarios), falling back to any imported
	// scenario if "Default.zip" is named differently on this install.
	private static string ResolveScenario(string scenarioPathOrNull)
	{
		if (!string.IsNullOrEmpty(scenarioPathOrNull) &&
			!string.Equals(scenarioPathOrNull, "default", StringComparison.OrdinalIgnoreCase))
			return scenarioPathOrNull;

		string dir = Path.Combine(Application.persistentDataPath, "Scenarios");
		string def = Path.Combine(dir, "Default.zip");
		if (File.Exists(def))
			return def;
		if (GameManager.defaultScenarios != null)
		{
			foreach (string name in GameManager.defaultScenarios)
			{
				string p = Path.Combine(dir, name + ".zip");
				if (File.Exists(p))
					return p;
			}
		}
		if (Directory.Exists(dir))
		{
			string[] zips = Directory.GetFiles(dir, "*.zip");
			if (zips.Length > 0)
				return zips[0];
		}
		return def;   // let LoadScenario throw a clear FileNotFoundException
	}

	// Mirrors what ScenarioSelectorPanel does under the hood, minus the UI:
	// deserialize the scenario's settings into ScenarioSettings.Instance and
	// extract its bundled bibite templates so spawn settings can resolve them.
	private static void LoadScenario(string scenarioZipPath)
	{
		if (!File.Exists(scenarioZipPath))
			throw new FileNotFoundException("scenario not found: " + scenarioZipPath);

		using (FileStream fs = File.OpenRead(scenarioZipPath))
		using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
		{
			JObject info = ReadEntry(zip, "scenario.info");
			Utility.Version version = ParseVersion(info);
			JObject settings = SaveSystem.GetSettingsOfSave(zip);
			if (settings == null)
				throw new InvalidDataException("scenario has no settings.bb8settings: " + scenarioZipPath);

			SerializationHelper.DeserializeScenario(settings, version, checkForModifiers: true);

			// Writes any novel templates to disk before its (UI) popup, so a throw
			// here is non-fatal for spawning — log and continue.
			try { SaveSystem.CheckTemplatesOfArchive(zip); }
			catch (Exception e) { Debug.LogWarning("[BibiControl] CheckTemplatesOfArchive: " + e.Message); }
		}
	}

	private static Utility.Version ParseVersion(JObject info)
	{
		string v = info != null && info["version"] != null ? info["version"].ToString() : null;
		if (string.IsNullOrEmpty(v))
			v = Application.version;
		try { return Utility.Version.Parse(v); }
		catch { return Utility.Version.Parse(Application.version); }
	}

	private static JObject ReadEntry(ZipArchive zip, string name)
	{
		ZipArchiveEntry e = zip.GetEntry(name);
		if (e == null)
			return null;
		using (Stream s = e.Open())
		using (StreamReader r = new StreamReader(s))
			return JObject.Parse(r.ReadToEnd());
	}

	private static void EnsureParentDir(string path)
	{
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);
	}

	private static void Fail(MintResult res, Exception e)
	{
		res.Error = e;
		res.Done = true;
	}
}

// Hosts the mint coroutine for the one-shot launch mode (-bibiteMintSeed) and
// quits the process when the save is written. DontDestroyOnLoad so it survives
// the scene change MintSeedSave triggers.
public sealed class MintRunner : MonoBehaviour
{
	private string _scenario;
	private string _outPath;
	private bool _started;

	public void Begin(string scenario, string outPath)
	{
		_scenario = scenario;
		_outPath = outPath;
	}

	// Called once the menu scene is up (the engine has initialized and imported
	// its bundled scenarios). Safe to call more than once; only the first runs.
	public void RunOnce()
	{
		if (_started)
			return;
		_started = true;
		StartCoroutine(Drive());
	}

	private IEnumerator Drive()
	{
		SeedMinter.MintResult res = new SeedMinter.MintResult();
		yield return StartCoroutine(SeedMinter.MintSeedSave(_scenario, _outPath, res));
		if (res.Error != null)
		{
			Debug.LogError("[BibiControl] mint failed: " + res.Error);
			Application.Quit(1);
		}
		else
		{
			Debug.Log("[BibiControl] mint wrote " + res.Path);
			Application.Quit(0);
		}
	}
}
