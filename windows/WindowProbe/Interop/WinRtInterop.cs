namespace WindowProbe.Interop;

/// <summary>
/// Descarte best-effort de objetos WinRT projetados. A projeção C#/WinRT pode
/// expor IClosable como IDisposable; quando isso não acontece, tentamos Close()
/// por reflexão. Falhas de descarte nunca devem mascarar o erro original.
/// </summary>
internal static class WinRtInterop
{
    public static void CloseQuietly(object? winrtObject)
    {
        if (winrtObject is null)
        {
            return;
        }

        try
        {
            if (winrtObject is IDisposable disposable)
            {
                disposable.Dispose();
                return;
            }

            var closeMethod = winrtObject.GetType().GetMethod("Close", Type.EmptyTypes);
            if (closeMethod is not null && closeMethod.ReturnType == typeof(void))
            {
                closeMethod.Invoke(winrtObject, null);
            }
        }
        catch
        {
            // Best-effort: o chamador já está em um caminho de erro ou encerramento.
        }
    }
}
