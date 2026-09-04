using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>Uma mensagem que o funil jogou fora, e por quê — regra 6 do vigia.</summary>
/// <param name="De">Remetente. É o suficiente para o usuário reconhecer o que perdeu.</param>
/// <param name="Assunto">Assunto, como veio.</param>
/// <param name="Motivo">O degrau que a descartou.</param>
public sealed record Descartada(string De, string Assunto, string Motivo);

/// <summary>
/// O resultado de uma passada do vigia.
/// <para>
/// A lista de descartados vai junto de propósito: sem poder conferir o que a triagem jogou
/// fora, ela vira confiança falsa — e confiança falsa num ponto fraco reconhecido é pior que
/// nenhuma triagem. É a regra 6.
/// </para>
/// <para>
/// Note o que NÃO está aqui: corpo de mensagem. O que sai da triagem é veredito, e é isso que
/// mantém a regra 3 de pé quando este objeto encosta na tela.
/// </para>
/// </summary>
public sealed record DigestoDeEmail(
    IReadOnlyList<MailSummary> Itens,
    int Lidas,
    IReadOnlyList<Descartada> Descartadas,
    IReadOnlyList<Rajada> Rajadas,
    DateTime QuandoUtc)
{
    public static DigestoDeEmail Vazio => new(
        Array.Empty<MailSummary>(), 0, Array.Empty<Descartada>(),
        Array.Empty<Rajada>(), DateTime.UtcNow);

    public int Urgentes => Itens.Count(i => i.Urgency != MailUrgency.Baixa);

    public bool TemAlgoADizer => Itens.Count > 0 || Rajadas.Count > 0;

    /// <summary>
    /// A frase que o Shadow fala.
    /// <para>
    /// Montada por CÓDIGO, a partir dos números apurados — não pelo modelo. Os números são
    /// exatos porque não passaram por ele; se o 9B alucinar, alucina no resumo de uma mensagem,
    /// não na contagem que abre o aviso. É a mesma divisão da rajada: fatos do código, prosa do
    /// modelo.
    /// </para>
    /// </summary>
    public string Frase()
    {
        if (Rajadas.Count > 0)
        {
            var r = Rajadas[0];
            int minutos = Math.Max(1, (int)r.Duracao.TotalMinutes);
            return $"{r.Quantas} alertas de {r.Nome} em {minutos} min, desde " +
                   $"{r.Comeco.ToLocalTime():HH:mm}. Ainda chegando.";
        }

        if (Itens.Count == 0)
            return Lidas == 0
                ? "Nada novo na caixa."
                : $"Li {Lidas} e-mail{(Lidas == 1 ? "" : "s")}. Nenhum pede você agora.";

        string lidas = $"Li {Lidas} e-mail{(Lidas == 1 ? "" : "s")}.";

        return Urgentes == 0
            ? $"{lidas} {Itens.Count} para quando puder."
            : $"{lidas} {Urgentes} precisa{(Urgentes == 1 ? "" : "m")} de você.";
    }
}
