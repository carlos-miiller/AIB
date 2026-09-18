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
/// A chave é a tupla <c>(string Tool, string Cmd, string? ContentHash)</c> — a igualdade
/// estrutural embutida em <c>ValueTuple&lt;string,string,string?&gt;</c> faz match exato
/// byte-a-byte em cada elemento (ordinal). <c>Tool</c> é o nome da ferramenta que pediu
/// confirmação (ver <see cref="Ferramentas"/>); <c>Cmd</c> é o texto exato exibido no cartão.
///
/// <c>ContentHash</c> está reservado e hoje é sempre null: o registry não calcula hash do
/// script de skill. Consequência: editar o script de uma skill em disco NÃO invalida um
/// "Sempre permitir" dado a ela nesta sessão — a chave é só ferramenta + comando.
/// </summary>
public static class AlwaysAllowSession
{
    private static readonly HashSet<(string Tool, string Cmd, string? ContentHash)> _allowed = new();

    public static bool Contains((string Tool, string Cmd, string? ContentHash) key)
    {
        lock (_allowed) return _allowed.Contains(key);
    }

    public static void Add((string Tool, string Cmd, string? ContentHash) key)
    {
        lock (_allowed) _allowed.Add(key);
    }

    public static void Clear()
    {
        lock (_allowed) _allowed.Clear();
    }

    /// <summary>
    /// O que está autorizado agora, para a tela poder mostrar.
    /// <para>
    /// Uma allowlist que o usuário não consegue VER é uma decisão de segurança tomada por ele e
    /// depois escondida dele. Ordenada por ferramenta e comando para a lista não dançar entre
    /// duas leituras.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Tool, string Cmd, string? ContentHash)> Listar()
    {
        lock (_allowed)
        {
            var copia = new List<(string Tool, string Cmd, string? ContentHash)>(_allowed);
            copia.Sort((a, b) =>
            {
                int t = string.CompareOrdinal(a.Tool, b.Tool);
                return t != 0 ? t : string.CompareOrdinal(a.Cmd, b.Cmd);
            });
            return copia;
        }
    }

    /// <summary>Quantos comandos estão autorizados nesta sessão.</summary>
    public static int Quantos
    {
        get { lock (_allowed) return _allowed.Count; }
    }
}
