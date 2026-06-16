package ipc

// Sim-control command vocabulary spoken over a compat Session. The game-side DLL
// registers one handler per command; the control plane sends each as a request
// (KindRequest) and decodes the typed result from the response.
//
// These are the wire contract. The C# DLL (dll/BibiControl) and the Go simctl
// client must agree on the command names and the JSON field names below. To add
// a command: add a constant here, the payload/result type(s), a method on
// simctl.Client, and a handler on the DLL side.
const (
	CommandStop    = "STOP"
	CommandResume  = "RESUME"
	CommandInfo    = "INFO"
	CommandReload  = "RELOAD"
	CommandNewSave = "NEWSAVE"
)

// StopResult is returned by STOP. PreviousTimeScale is the simulation speed that
// was configured before the pause, so a later RESUME can restore it.
type StopResult struct {
	PreviousTimeScale float64 `json:"previous_time_scale"`
}

// ResumeRequest is the payload for RESUME. TimeScale is the speed multiplier the
// simulation should run at (1.0 is the real-time baseline). Must be > 0.
type ResumeRequest struct {
	TimeScale float64 `json:"time_scale"`
}

// ResumeResult echoes the time scale the simulation actually resumed at.
type ResumeResult struct {
	TimeScale float64 `json:"time_scale"`
}

// AutosaveInfo describes the most recent autosave the game has written. It is
// nil when the game has not produced any autosave yet.
type AutosaveInfo struct {
	Path         string `json:"path,omitempty"`
	Name         string `json:"name,omitempty"`
	ModifiedUnix int64  `json:"modified_unix,omitempty"`
	Time         string `json:"time,omitempty"`
}

// InfoResult is returned by INFO. The field set is expected to grow; keep new
// fields optional (omitempty / pointer) so older clients keep decoding.
type InfoResult struct {
	// TPS is the configured target simulation ticks-per-second.
	TPS float64 `json:"tps"`
	// RealTPS is the measured (achieved) simulation ticks-per-second.
	RealTPS float64 `json:"real_tps"`
	// Paused is true when the simulation time scale is 0.
	Paused bool `json:"paused"`
	// SimTime is the total simulated time in seconds.
	SimTime float64 `json:"sim_time,omitempty"`
	// LastAutosave is the newest autosave file, or nil if none exists.
	LastAutosave *AutosaveInfo `json:"last_autosave,omitempty"`
}

// ReloadResult is returned by RELOAD. Save is the file the game began reloading.
type ReloadResult struct {
	Save string `json:"save,omitempty"`
	Ok   bool   `json:"ok"`
}

// NewSaveRequest is the payload for NEWSAVE. Scenario selects what to mint from:
// an absolute path to a scenario .zip, or "" / "default" for the game's bundled
// Default scenario. Out is the absolute destination path for the minted save
// .zip (required).
type NewSaveRequest struct {
	Scenario string `json:"scenario,omitempty"`
	Out      string `json:"out"`
}

// NewSaveResult is returned by NEWSAVE. Path is the save file that was written.
type NewSaveResult struct {
	Path string `json:"path,omitempty"`
	Ok   bool   `json:"ok"`
}
