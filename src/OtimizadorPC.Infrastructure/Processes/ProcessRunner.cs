using System.Diagnostics;
using System.Text;

namespace OtimizadorPC.Infrastructure.Processes;

public sealed record ProcessOutcome(int ExitCode, string Output);

/// <summary>Executa ferramentas do Windows (dism, powercfg, takeown...) sem abrir janela.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(string fileName, string arguments,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Ferramentas de console do Windows escrevem na página de código OEM
            StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            StandardErrorEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        void Handle(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            onLine?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }

        return new ProcessOutcome(process.ExitCode, output.ToString());
    }

    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
