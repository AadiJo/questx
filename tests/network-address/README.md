# Local network address checks

Run `dotnet run --project tests/network-address/NetworkAddressChecks.csproj` from the repository root (.NET 8+; set `DOTNET_ROLL_FORWARD=Major` on newer runtimes only).

Compiles the production `LocalAddressMonitor` with a stub logger and a scripted resolver and clock. These checks cover:

- Stable selection: reordered multi-address results never alternate, the current address is kept while it stays assigned, the route's preferred address wins, and the fallback choice does not depend on input order.
- Filtering: loopback, unspecified, broadcast and IPv6 addresses are ignored, and link-local addresses come last.
- Failures: lookup errors keep the last address and log once per failure streak, a genuine loss clears the address and the address can reappear, and recovery works after errors.
- Threading: a slow lookup never blocks `Poll`, only one lookup runs at a time, results are applied and `Changed` is raised only from `Poll`, and the next lookup waits for the refresh interval.
- Lookup policy: a usable route answer skips interface enumeration and DNS, interfaces come before host-name DNS, and "no address" requires a clean negative answer. Any other failure throws so the address is kept.

These checks do not exercise real sockets, `NetworkInterface` on Android/IL2CPP, Unity callbacks, or DNS latency on the headset.
