// Package simctl is a typed client for the simulation-control commands defined
// in the ipc package (STOP / RESUME / INFO / RELOAD).
//
// It wraps anything that can send a request and decode a reply — an
// *ipc.Session, an *ipc.OpaqueNode, or a *noderuntime.Runtime all satisfy the
// Requester interface — and exchanges the request/response pairs with the
// game-side DLL (dll/BibiControl).
//
// Extending: add a constant + payload/result type(s) in the ipc package, then a
// thin method here, and a matching handler on the DLL side. The transport
// (ipc.Session) does not change.
package simctl

import (
	"context"

	"github.com/asemones/bibicontrol/ipc"
)

// Requester is the minimal surface simctl needs: send a command request and
// decode the reply into out. It matches ipc.Session.Request,
// ipc.OpaqueNode.Request and noderuntime.Runtime.Request.
type Requester interface {
	Request(ctx context.Context, command string, payload any, out any) error
}

// Client issues typed sim-control commands over a Requester.
type Client struct {
	r Requester
}

// New returns a Client that sends commands through r.
func New(r Requester) *Client { return &Client{r: r} }

// Stop pauses the simulation (sets time scale to 0) and returns the speed it was
// configured at, so a later Resume can restore it.
func (c *Client) Stop(ctx context.Context) (ipc.StopResult, error) {
	var out ipc.StopResult
	err := c.r.Request(ctx, ipc.CommandStop, nil, &out)
	return out, err
}

// Resume runs the simulation at the given time scale. timeScale must be > 0.
func (c *Client) Resume(ctx context.Context, timeScale float64) (ipc.ResumeResult, error) {
	var out ipc.ResumeResult
	err := c.r.Request(ctx, ipc.CommandResume, ipc.ResumeRequest{TimeScale: timeScale}, &out)
	return out, err
}

// Info returns live telemetry: target and real TPS, paused state, sim time, and
// the most recent autosave.
func (c *Client) Info(ctx context.Context) (ipc.InfoResult, error) {
	var out ipc.InfoResult
	err := c.r.Request(ctx, ipc.CommandInfo, nil, &out)
	return out, err
}

// Reload tells the game to reload its most recent save file.
func (c *Client) Reload(ctx context.Context) (ipc.ReloadResult, error) {
	var out ipc.ReloadResult
	err := c.r.Request(ctx, ipc.CommandReload, nil, &out)
	return out, err
}

// NewSave mints a fresh save from a scenario (pass "" or "default" for the
// game's bundled default) and writes it to out (an absolute .zip path),
// returning the path actually written. Minting loads a scenario, starts a new
// game, and runs the engine's save coroutine, so pass a context with a generous
// deadline (tens of seconds).
func (c *Client) NewSave(ctx context.Context, scenario, out string) (ipc.NewSaveResult, error) {
	var res ipc.NewSaveResult
	err := c.r.Request(ctx, ipc.CommandNewSave, ipc.NewSaveRequest{Scenario: scenario, Out: out}, &res)
	return res, err
}
