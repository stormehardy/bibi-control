package simctl

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/asemones/bibicontrol/ipc"
)

// fakeSim is an in-process stand-in for the game-side DLL. It speaks the exact
// envelope contract the real DLL must implement (a request with a command +
// optional payload in, a response with reply_to set and a JSON payload out), so
// these tests double as executable documentation of that contract.
type fakeSim struct {
	sess *ipc.Session

	mu          sync.Mutex
	lastCommand string
	lastResume  ipc.ResumeRequest
	lastNewSave ipc.NewSaveRequest
}

func newFakeSim(conn net.Conn) *fakeSim {
	f := &fakeSim{sess: ipc.NewSession(conn, nil)}
	go f.serve()
	return f
}

func (f *fakeSim) serve() {
	for env := range f.sess.Events() {
		if env.Kind != ipc.KindRequest {
			continue
		}
		payload, errMsg := f.handle(env)
		reply := ipc.Envelope{Kind: ipc.KindResponse, ReplyTo: env.ID}
		if errMsg != "" {
			reply.Kind = ipc.KindError
			reply.Error = errMsg
		} else {
			reply.Payload = payload
		}
		_ = f.sess.Send(context.Background(), reply)
	}
}

func (f *fakeSim) handle(env ipc.Envelope) (json.RawMessage, string) {
	f.mu.Lock()
	f.lastCommand = env.Command
	f.mu.Unlock()

	switch env.Command {
	case ipc.CommandStop:
		return mustJSON(ipc.StopResult{PreviousTimeScale: 3.5}), ""
	case ipc.CommandResume:
		var req ipc.ResumeRequest
		if err := json.Unmarshal(env.Payload, &req); err != nil {
			return nil, err.Error()
		}
		if req.TimeScale <= 0 {
			return nil, "time_scale must be > 0"
		}
		f.mu.Lock()
		f.lastResume = req
		f.mu.Unlock()
		return mustJSON(ipc.ResumeResult{TimeScale: req.TimeScale}), ""
	case ipc.CommandInfo:
		return mustJSON(ipc.InfoResult{
			TPS:     60,
			RealTPS: 58.25,
			Paused:  true,
			SimTime: 1234.5,
			LastAutosave: &ipc.AutosaveInfo{
				Path:         "/saves/Autosaves/autosave_20260615.zip",
				Name:         "autosave_20260615.zip",
				ModifiedUnix: 1700000000,
				Time:         "2026-06-15T12:00:00.0000000Z",
			},
		}), ""
	case ipc.CommandReload:
		return mustJSON(ipc.ReloadResult{Save: "/saves/Autosaves/autosave_20260615.zip", Ok: true}), ""
	case ipc.CommandNewSave:
		var req ipc.NewSaveRequest
		if err := json.Unmarshal(env.Payload, &req); err != nil {
			return nil, err.Error()
		}
		if req.Out == "" {
			return nil, "out (destination .zip path) is required"
		}
		f.mu.Lock()
		f.lastNewSave = req
		f.mu.Unlock()
		return mustJSON(ipc.NewSaveResult{Path: req.Out, Ok: true}), ""
	default:
		return nil, "unknown command: " + env.Command
	}
}

func mustJSON(v any) json.RawMessage {
	b, err := json.Marshal(v)
	if err != nil {
		panic(err)
	}
	return b
}

// newClientServer wires a simctl.Client to a fakeSim over an in-memory pipe.
func newClientServer(t *testing.T) (*Client, *fakeSim, *ipc.Session) {
	t.Helper()
	cConn, sConn := net.Pipe()
	sim := newFakeSim(sConn)
	cli := ipc.NewSession(cConn, nil)
	t.Cleanup(func() {
		_ = cli.Close()
		_ = sim.sess.Close()
	})
	return New(cli), sim, cli
}

func testCtx(t *testing.T) context.Context {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	t.Cleanup(cancel)
	return ctx
}

func TestStop(t *testing.T) {
	c, sim, _ := newClientServer(t)
	res, err := c.Stop(testCtx(t))
	if err != nil {
		t.Fatalf("Stop: %v", err)
	}
	if res.PreviousTimeScale != 3.5 {
		t.Fatalf("PreviousTimeScale = %v, want 3.5", res.PreviousTimeScale)
	}
	sim.mu.Lock()
	defer sim.mu.Unlock()
	if sim.lastCommand != ipc.CommandStop {
		t.Fatalf("command = %q, want %q", sim.lastCommand, ipc.CommandStop)
	}
}

func TestResume(t *testing.T) {
	c, sim, _ := newClientServer(t)
	res, err := c.Resume(testCtx(t), 4.25)
	if err != nil {
		t.Fatalf("Resume: %v", err)
	}
	if res.TimeScale != 4.25 {
		t.Fatalf("TimeScale = %v, want 4.25", res.TimeScale)
	}
	sim.mu.Lock()
	defer sim.mu.Unlock()
	if sim.lastResume.TimeScale != 4.25 {
		t.Fatalf("server saw TimeScale = %v, want 4.25", sim.lastResume.TimeScale)
	}
}

func TestInfo(t *testing.T) {
	c, _, _ := newClientServer(t)
	res, err := c.Info(testCtx(t))
	if err != nil {
		t.Fatalf("Info: %v", err)
	}
	if res.TPS != 60 || res.RealTPS != 58.25 || !res.Paused || res.SimTime != 1234.5 {
		t.Fatalf("unexpected info: %+v", res)
	}
	if res.LastAutosave == nil || res.LastAutosave.Name != "autosave_20260615.zip" {
		t.Fatalf("unexpected autosave: %+v", res.LastAutosave)
	}
	if res.LastAutosave.ModifiedUnix != 1700000000 {
		t.Fatalf("modified_unix = %d", res.LastAutosave.ModifiedUnix)
	}
}

func TestReload(t *testing.T) {
	c, _, _ := newClientServer(t)
	res, err := c.Reload(testCtx(t))
	if err != nil {
		t.Fatalf("Reload: %v", err)
	}
	if !res.Ok || res.Save == "" {
		t.Fatalf("unexpected reload: %+v", res)
	}
}

func TestNewSave(t *testing.T) {
	c, sim, _ := newClientServer(t)
	res, err := c.NewSave(testCtx(t), "default", `C:\seeds\seed.zip`)
	if err != nil {
		t.Fatalf("NewSave: %v", err)
	}
	if !res.Ok || res.Path != `C:\seeds\seed.zip` {
		t.Fatalf("unexpected newsave: %+v", res)
	}
	sim.mu.Lock()
	defer sim.mu.Unlock()
	if sim.lastCommand != ipc.CommandNewSave {
		t.Fatalf("command = %q, want %q", sim.lastCommand, ipc.CommandNewSave)
	}
	if sim.lastNewSave.Out != `C:\seeds\seed.zip` || sim.lastNewSave.Scenario != "default" {
		t.Fatalf("server saw %+v", sim.lastNewSave)
	}
}

// TestNewSaveRequiresOut verifies the server rejects a NEWSAVE with no out path.
func TestNewSaveRequiresOut(t *testing.T) {
	c, _, _ := newClientServer(t)
	_, err := c.NewSave(testCtx(t), "default", "")
	if err == nil {
		t.Fatal("expected error when out is empty")
	}
}

// TestErrorResponse verifies that an error reply (error field set) surfaces as
// ipc.ErrRequestFailed, which is how the DLL reports a failed command.
func TestErrorResponse(t *testing.T) {
	_, _, cli := newClientServer(t)
	var out struct{}
	err := cli.Request(testCtx(t), "DOES_NOT_EXIST", nil, &out)
	if err == nil {
		t.Fatal("expected error for unknown command")
	}
	if !errors.Is(err, ipc.ErrRequestFailed) {
		t.Fatalf("error = %v, want ipc.ErrRequestFailed", err)
	}
}
