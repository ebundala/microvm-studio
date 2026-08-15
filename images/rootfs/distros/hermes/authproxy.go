// hermes-authproxy fronts the loopback-only `hermes serve` process with the
// platform's already-completed ingress authentication, the same way
// init.go's fileBrowserProxy fronts filebrowser: `hermes serve` is bound to
// 127.0.0.1 only, where Hermes' own dashboard auth gate never engages
// ("the auth gate engages on non-loopback binds" — see Hermes' own startup
// diagnostic), so this proxy is the ONLY thing reachable from outside the
// guest. It forwards a request only if the ingress proxy already
// authenticated the caller for this (gated-mode) agent and injected
// X-Mvm-User-Email — the same signed, unspoofable header filebrowser's
// proxy-auth trusts — otherwise it 403s. Unlike filebrowser, Hermes has no
// per-user account model to provision, so there is nothing to do beyond
// that presence check.
//
// A public-mode agent never receives X-Mvm-* headers at all (see
// the ingress proxy's design note), so a "hermes" agent must run
// gated for its dashboard to be reachable — same requirement filebrowser
// already has.
package main

import (
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
)

const (
	listenAddr  = "0.0.0.0:9119"
	publicPort  = "9119"
	targetAddr  = "http://127.0.0.1:9120"
	identityHdr = "X-Mvm-User-Email"
)

func main() {
	// AGENT_NAME comes from init.go's runBootHook, which passes it (along
	// with AGENT_ID/TENANT_ID) as env vars to on-boot.sh, which in turn
	// leaves it in the inherited environment when it execs us — not
	// hermes-specific plumbing on init.go's side, just identity any boot
	// hook might need. Without it we can't build the prefix below, so
	// Hermes' dashboard would still 404 on every asset even though the
	// Host-header/auth pieces work — log loudly rather than silently
	// serving a broken dashboard.
	agentName := os.Getenv("AGENT_NAME")
	if agentName == "" {
		log.Println("[hermes-authproxy] WARN: AGENT_NAME not set — dashboard asset URLs will be wrong (no X-Forwarded-Prefix)")
	}
	prefix := "/vms/" + agentName + "/p/" + publicPort

	target, err := url.Parse(targetAddr)
	if err != nil {
		log.Fatalf("[hermes-authproxy] parse target: %v", err) // unreachable: fixed, valid URL
	}
	proxy := httputil.NewSingleHostReverseProxy(target)
	// httputil's default Director rewrites URL.Scheme/Host/Path but leaves
	// the Host HEADER (req.Host) as whatever the original inbound request
	// carried — here that's the tenant's public hostname
	// (tenant1.ebundala.dev), forwarded unchanged all the way from the
	// browser through nginx/YARP/the ingress proxy to us. Hermes'
	// own DNS-rebinding defense (GHSA-ppp5-vxwm-4cf7,
	// hermes_cli/web_server.py's host_header_middleware) rejects any Host
	// header that isn't the loopback name it's bound to, so without this
	// override every request 400s with "Invalid Host header" even though
	// it's perfectly legitimate — it's OUR loopback hop being mistaken for
	// the rebinding attack the check exists to catch.
	//
	// X-Forwarded-Prefix: Hermes' own dashboard_auth/prefix.py reads this
	// to rewrite the served index.html's asset URLs (and OAuth redirect_uri,
	// cookie Path) to include our ingress prefix — without it, Hermes emits
	// root-absolute asset paths like /assets/index-XXXX.js, which the
	// browser resolves against the page's actual origin
	// (https://tenant1.ebundala.dev/assets/...) instead of under
	// /vms/<name>/p/9119/, 404ing since the ingress proxy only
	// routes the /vms/* prefix. Same class of bug filebrowser's
	// --baseURL flag (see init.go's startFileBrowserServer) exists to
	// avoid; Hermes' equivalent is this header instead of a CLI flag.
	// Origin: Hermes' own WebSocket upgrade guard (hermes_cli/web_server.py's
	// _ws_host_origin_reason, backing /api/events, /api/ws, /api/console,
	// /api/pty) checks Host against bound_host (handled by the override
	// above) and THEN, separately, checks the browser's real Origin header
	// against that same bound_host — but Origin is never rewritten by
	// httputil's default Director, so it still carries the tenant's public
	// origin (https://tenant1.ebundala.dev) while bound_host is Hermes' own
	// loopback bind (127.0.0.1, from on-boot.sh's `--host 127.0.0.1`).
	// Those can never match for any reverse-proxied deployment, so every
	// WebSocket upgrade 4403s with origin_mismatch even though the request
	// is legitimate — the exact same class of problem the Host override
	// above already solves, just unaddressed for Origin. Confirmed against
	// the installed hermes_cli/web_server.py: an ABSENT Origin header is
	// explicitly treated as a trusted non-browser client ("if not origin:
	// return None" — the same allowance made for a packaged Electron
	// client's file://null origin) and short-circuits straight to allowed,
	// so dropping the header entirely — not rewriting it to a fake matching
	// value — is the correct fix: we're not spoofing an origin, we're
	// omitting one for a hop Hermes' own logic already trusts by default.
	originalDirector := proxy.Director
	proxy.Director = func(r *http.Request) {
		originalDirector(r)
		r.Host = target.Host
		r.Header.Del("Origin")
		r.Header.Set("X-Forwarded-Prefix", prefix)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get(identityHdr) == "" {
			http.Error(w, "hermes dashboard requires gated ingress (no identity header present)", http.StatusForbidden)
			return
		}
		proxy.ServeHTTP(w, r)
	})

	srv := &http.Server{Addr: listenAddr, Handler: mux}
	log.SetOutput(os.Stdout)
	log.Printf("[hermes-authproxy] listening on %s -> %s (trusting %s, prefix %s)", listenAddr, targetAddr, identityHdr, prefix)
	log.Fatal(srv.ListenAndServe())
}
