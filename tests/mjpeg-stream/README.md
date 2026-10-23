# MJPEG stream checks

Run `dotnet run --project tests/mjpeg-stream/MjpegStreamChecks.csproj` from the repository root (.NET 8+).

Compiles the production `MjpegStream` helper used by `/video`. These checks use a fake monotonic clock, a scripted frame source, and a controlled output stream. Each write can advance the clock to model a slow client, and each wait can wake late to model a delayed timer.

- **Framing:** each part has the exact multipart bytes, boundary, and `Content-Length`. Each part is one header write, one write of the original JPEG array, and one flush. Repeated, older, and data-less frames are not sent, and an unavailable source writes nothing.
- **Pacing:** polls stay one period apart because write time counts toward the period, and a poll with no new frame waits a full period. A rate change applies to the next period, and a zero rate falls back to 1 fps.
- **Slow output and late timers:** a slow write or a late timer wakeup re-anchors pacing at the next poll, so missed periods are skipped without back-to-back catch-up frames. A client that is always slow is served at its own write rate.
- **Ending:** cancellation during a write or a wait ends the stream cleanly. An `IOException` ends it as a disconnect, while unexpected exceptions and cancellation not caused by the request still surface.
- **Real clock:** with the production `Stopwatch` and `Task.Delay` overload, cancelling the request interrupts a 1 fps wait.

These checks do not exercise EmbedIO chunked encoding, real sockets, client registration limits, or on-device throughput.
