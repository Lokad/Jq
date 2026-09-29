# Test baseline

The `JqTests*.cs` files cover command execution, formatting, conditionals, select,
test/gsub regex behavior, and resource limits. They run against the built
Lokad.Jq assembly using a small in-memory `IJqHost`. White-box tests have friend
access through the library's AssemblyInfo.cs.

These tests capture an initial behavior baseline, not an upstream conformance
certificate. `HostBoundaryTests` covers standalone invocation, path, descriptor,
and cleanup behavior. Shell parsing and orchestration belong to embedding
applications and are outside this test host's responsibilities.

Add independent behavior cases for each compatibility increment. Keep test
expectations fixed and reviewable. Differential probes, when introduced, must
be opt-in and use an explicitly installed reference executable; ordinary tests
must run without external repositories or native jq on PATH.