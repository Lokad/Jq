# Test baseline

The `JqTests*.cs` files hold fixed behavior cases (input, filter, expected
stdout/stderr/status) run against the built Lokad.Jq assembly using a small
in-memory `IJqHost`. Areas follow the compatibility matrix: core values and
operators, bindings and destructuring, functions and closures, errors and
control flow, paths and assignment, reductions and iteration, collections and
ordering, strings and encodings, regex, math, dates and times, modules, inputs
and streaming, and command options with output modes. Focused white-box tests
cover parser, value, budget, and regex-cache contracts that are hard to
diagnose through command output. Seeded fuzz campaigns (value, CLI,
multi-input, and path-mode shapes, plus a fixed deep-value matrix)
assert staged exits, and `HostBoundaryTests` covers invocation,
descriptors, ownership, and cleanup.
White-box tests have friend access through the library's AssemblyInfo.cs.

These tests capture an initial behavior baseline, not an upstream conformance
certificate. `HostBoundaryTests` covers standalone invocation, path, descriptor,
and cleanup behavior. Shell parsing and orchestration belong to embedding
applications and are outside this test host's responsibilities.

Add independent behavior cases for each compatibility increment. Keep test
expectations fixed and reviewable. Differential probes, when introduced, must
be opt-in and use an explicitly installed reference executable; ordinary tests
must run without external repositories or native jq on PATH.