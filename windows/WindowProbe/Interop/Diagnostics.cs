using System.Text;

namespace WindowProbe.Interop;

internal static class Diagnostics
{
    private const int CorException = unchecked((int)0x80131500);

    public static string Describe(Exception exception)
    {
        var builder = new StringBuilder(exception.Message);
        AppendHResult(builder, exception.HResult);

        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            builder.Append(" → ").Append(inner.Message);
            AppendHResult(builder, inner.HResult);
        }

        return builder.ToString();
    }

    public static string HintFor(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string hint = HintForHResult(current.HResult);
            if (hint.Length > 0)
            {
                return hint;
            }
        }

        return string.Empty;
    }

    private static void AppendHResult(StringBuilder builder, int hresult)
    {
        if (hresult != 0 && hresult != CorException)
        {
            builder.Append($" [HRESULT 0x{hresult:X8}]");
        }
    }

    private static string HintForHResult(int hresult) => hresult switch
    {
        unchecked((int)0x80070005) =>
            "Acesso negado. Janelas de processos elevados/protegidos podem recusar a captura.",
        unchecked((int)0x80070057) =>
            "Janela inválida ou fechada durante a captura.",
        unchecked((int)0x80004005) =>
            "Falha genérica do Windows. A janela pode estar protegida contra captura (WDA_EXCLUDEFROMCAPTURE) ou já ter sido fechada.",
        unchecked((int)0x8007000E) =>
            "Memória insuficiente para concluir a captura.",
        unchecked((int)0x8001010E) =>
            "Objeto WinRT acessado na thread errada.",
        unchecked((int)0x800401F0) =>
            "COM/WinRT não inicializado nesta thread.",
        unchecked((int)0x80070490) =>
            "Elemento não encontrado: a janela ou o dispositivo pode ter sido removido.",
        _ => string.Empty,
    };
}
