using System.Collections.Generic;

namespace AIB.Services;

/// <summary>
/// Allowlist em memória dos comandos que o usuário marcou como "Sempre permitir"
/// nesta sessão do AIB. Vivo apenas enquanto o processo estiver de pé.
///
/// Não persiste em disco — sessão única; quando o app fecha, a lista zera.
/// O escopo "sessão" é uma exigência explícita do D2 do fase 01 para impedir
/// que um clique acidental autorize um comando perigoso para sempre.
///
/// Match é exato byte-a-byte (sem normalização de case ou whitespace).
/// </summary>
public static class AlwaysAllowSession
{
    private static readonly HashSet<string> _allowed = new();

    public static bool Contains(string command)
    {
        lock (_allowed) return _allowed.Contains(command);
    }

    public static void Add(string command)
    {
        lock (_allowed) _allowed.Add(command);
    }

    public static void Clear()
    {
        lock (_allowed) _allowed.Clear();
    }
}
