using System;
using System.Collections.Generic;

namespace AIB.Services.Navegador;

/// <summary>
/// O nome de um elemento da página pela ref dele — <c>s21e17</c> vira <c>botão "OK"</c> —, para
/// a TELA: o chip da ferramenta e o registro de ações.
/// <para>
/// Visto no uso: as ações do navegador apareciam como "click s21e17", que não diz nada a quem
/// lê. O cartão de confirmação já mostrava o nome; o chip saía só dos argumentos da chamada.
/// </para>
/// <para>
/// Guarda o que já resolveu, porque a ref é de UMA leitura: depois do clique a página é lida de
/// novo, a ref antiga deixa de existir, e o chip da ação terminada voltaria ao código.
/// </para>
/// <para>
/// SÓ PARA A TELA. O nome é texto da página, conteúdo de terceiros: não vai para o resumo que a
/// memória guarda nem para o prompt (<see cref="Memory.ArtifactExtractor.ResumirArgumento"/>
/// continua devolvendo a ref).
/// </para>
/// </summary>
public static class NomesDeElemento
{
    private const int Teto = 2000;

    private static readonly Dictionary<string, string> Vistos = new(StringComparer.Ordinal);
    private static readonly object Trava = new();

    /// <summary>
    /// Quem resolve a ref na leitura atual: a ref completa e o nome, ou null quando ela não é
    /// desta leitura. Ligado pela ferramenta do navegador.
    /// </summary>
    public static Func<string, (string Ref, string Nome)?>? Fonte { get; set; }

    /// <summary>O nome do elemento, ou null quando a ref nunca foi vista.</summary>
    public static string? De(string? refe)
    {
        string r = (refe ?? "").Trim().ToLowerInvariant();
        if (r.Length == 0) return null;

        (string Ref, string Nome)? vivo = null;
        try { vivo = Fonte?.Invoke(r); } catch { /* a tela não cai por causa de um rótulo */ }

        lock (Trava)
        {
            if (vivo is { } achado)
            {
                if (Vistos.Count >= Teto) Vistos.Clear();
                Vistos[achado.Ref] = achado.Nome;
                return achado.Nome;
            }

            // Só a ref completa: a curta (e17) vale para a leitura do momento, e a de agora não
            // é mais a de quando a ação foi pedida.
            return Vistos.TryGetValue(r, out var nome) ? nome : null;
        }
    }

    /// <summary>O nome como a tela mostra: papel e texto, com o texto cortado.</summary>
    public static string Nome(NoDaPagina no)
    {
        string texto = no.Texto.Length > 60 ? no.Texto[..59] + "…" : no.Texto;
        return texto.Length > 0 ? $"{no.Papel} \"{texto}\"" : no.Papel;
    }

    /// <summary>Esquece tudo. Para o ensaio.</summary>
    public static void Limpar()
    {
        lock (Trava) Vistos.Clear();
    }
}
