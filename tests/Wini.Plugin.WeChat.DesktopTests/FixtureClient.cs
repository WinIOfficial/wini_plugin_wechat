using System.Diagnostics;
using System.IO.Pipes;

internal static class ListenerChecks
{
    internal sealed class FixtureClient(Process process, NamedPipeClientStream pipe, StreamWriter writer, StreamReader reader, nint window, string ready) : IDisposable
    {
        public Process Process { get; } = process;
        public nint Window { get; } = window;
        public static FixtureClient Start(string executable, string? accessibilityModule = null)
        {
            var token = Guid.NewGuid().ToString("N");
            var ready = Path.Combine(AppContext.BaseDirectory, token + ".ready");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(token); start.ArgumentList.Add(ready);
            if (accessibilityModule is not null) start.ArgumentList.Add(accessibilityModule);
            var process = Process.Start(start)!;
            var pipe = new NamedPipeClientStream(".", token, PipeDirection.InOut);
            try
            {
                pipe.Connect(8000);
                var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var reader = new StreamReader(pipe, leaveOpen: true);
                if (!long.TryParse(reader.ReadLine(), out var window) || window == 0)
                    throw new InvalidDataException("The WeChat fixture did not provide its window handle.");
                return new(process, pipe, writer, reader, (nint)window, ready);
            }
            catch { if (!process.HasExited) process.Kill(); pipe.Dispose(); process.Dispose(); throw; }
        }
        public void Send(string command)
        {
            writer.WriteLine(command);
            if (reader.ReadLine() != "ok") throw new EndOfStreamException("The WeChat fixture did not acknowledge the command.");
        }
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                try { Send("quit"); } catch (IOException) { }
                if (!Process.WaitForExit(3000)) Process.Kill();
            }
            reader.Dispose(); writer.Dispose(); pipe.Dispose(); Process.Dispose();
            if (File.Exists(ready)) File.Delete(ready);
        }
    }
}
