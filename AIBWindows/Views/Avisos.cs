using System;
using System.Collections.Generic;
using System.Linq;
using AIB.Services;
using AIB.Services.Mail;

namespace AIB.Views;

/// <summary>
/// O que cada aviso diz — mockup "Janelas flutuantes do AIB", família "Aviso" (16/10/2026).
/// <para>
/// Puro: devolve a receita e a vez na fila, e não abre janela nenhuma. Os botões chamam as
/// ações que o App passa (<see cref="Acoes"/>), porque é o App que conhece a janela de chat e o
/// orbe.
/// </para>
/// <para>
/// Nasceu de um pedido do usuário: "vamos remover todo o input de e-mail dentro da orbe". A
/// triagem e os lembretes entravam como FALA no orbe (e na conversa dele); agora vêm aqui. O
/// anel girando enquanto a triagem lê fica no orbe, por decisão dele.
/// </para>
/// </summary>
public static class Avisos
{
    /// <summary>Por quanto tempo o aviso da triagem comum fica antes de fechar sozinho.</summary>
    public static readonly TimeSpan PrazoDaTriagem = TimeSpan.FromSeconds(30);

    /// <summary>Quantas linhas de e-mail cabem no aviso; o resto fica em "+N na caixa".</summary>
    public const int LinhasNoAviso = 3;

    /// <summary>O que os botões fazem. Quem preenche é o App.</summary>
    public sealed record Acoes(
        string NomeDaIA,
        Action<MailSummary> AbrirComIA,
        Action<MailSummary?> VerNaCaixa,
        Action<Lembrete> AbrirEmailDoLembrete,
        Action AbrirShadow,
        Action<Lembrete> Adiar);

    /// <summary>
    /// O aviso de um digest: rajada, resposta a uma conversa sua ou triagem comum, nessa ordem
    /// de importância. Nulo quando não há o que dizer.
    /// </summary>
    public static (AvisoWindow.Receita Receita, VezDaJanela Vez)? DeDigesto(DigestoDeEmail d, Acoes a)
    {
        if (d == null || !d.TemAlgoADizer) return null;

        var itens = d.Itens ?? Array.Empty<MailSummary>();
        var primeiro = itens.FirstOrDefault();

        if (d.Rajadas.Count > 0)
        {
            return (new AvisoWindow.Receita(
                AvisoWindow.Tom.Perigo, "Rajada", AvisoWindow.IconeAlerta,
                Titulo: d.Frase(),
                Emails: itens.Take(1).ToList(),
                Botoes: Botoes(
                    primeiro == null ? null : new AvisoWindow.Botao($"Abrir com {a.NomeDaIA}", () => a.AbrirComIA(primeiro), Principal: true),
                    new AvisoWindow.Botao("Ver na caixa", () => a.VerNaCaixa(primeiro), Principal: primeiro == null))),
                VezDaJanela.Rajada);
        }

        // Resposta a uma conversa que VOCÊ começou, posta na tela pela sondagem antes do digest.
        if (itens.Count > 0 && itens.All(i => i.Iniciada && i.Description == MailDigestService.RespostaPendente))
        {
            string titulo = itens.Count == 1 ? "Responderam sua conversa" : $"{itens.Count} respostas às suas conversas";
            string sub = itens.Count == 1
                ? $"{primeiro!.Name} · {AvisoWindow.NomeDe(primeiro.De)} respondeu às {primeiro.LastMessageAt:HH:mm}."
                : "";

            return (new AvisoWindow.Receita(
                AvisoWindow.Tom.Azul, "Você iniciou", AvisoWindow.IconeSaida,
                Titulo: titulo, Sub: sub,
                Emails: itens.Count == 1 ? null : itens.Take(LinhasNoAviso).ToList(),
                Botoes: Botoes(
                    new AvisoWindow.Botao($"Abrir com {a.NomeDaIA}", () => a.AbrirComIA(primeiro!), Principal: true),
                    new AvisoWindow.Botao("Ver na caixa", () => a.VerNaCaixa(primeiro)))),
                VezDaJanela.RespostaSua);
        }

        int urgentes = itens.Count(i => i.Urgency == MailUrgency.Maxima);
        string tituloDaTriagem = (itens.Count == 1 ? "1 e-mail pede você" : $"{itens.Count} e-mails pedem você")
                                 + (urgentes > 0 ? $", {urgentes} urgente{(urgentes == 1 ? "" : "s")}" : "");
        int fora = Math.Max(0, itens.Count - LinhasNoAviso);

        return (new AvisoWindow.Receita(
            AvisoWindow.Tom.Lilas, "E-mail", AvisoWindow.IconeEnvelope,
            Titulo: tituloDaTriagem,
            Sub: d.Lidas > 0 ? $"Li {d.Lidas} e-mail{(d.Lidas == 1 ? "" : "s")}." + (fora > 0 ? $" Mais {fora} na caixa." : "") : "",
            Emails: itens.Take(LinhasNoAviso).ToList(),
            Botoes: itens.Count == 1
                ? Botoes(
                    new AvisoWindow.Botao($"Abrir com {a.NomeDaIA}", () => a.AbrirComIA(primeiro!), Principal: true),
                    new AvisoWindow.Botao("Ver na caixa", () => a.VerNaCaixa(primeiro)))
                : Botoes(
                    new AvisoWindow.Botao("Ver na caixa", () => a.VerNaCaixa(null), Principal: true),
                    new AvisoWindow.Botao("Depois", () => { })),
            FechaSozinho: PrazoDaTriagem),
            VezDaJanela.Triagem);
    }

    /// <summary>
    /// O aviso de um lembrete. A fala é a que a persona escreveu ao criar; o atrasado diz para
    /// quando era na pill, e não mais grudado na frente da fala.
    /// </summary>
    public static (AvisoWindow.Receita Receita, VezDaJanela Vez) DeLembrete(Lembrete l, DateTime agoraUtc, Acoes a)
    {
        bool atrasado = agoraUtc - l.QuandoUtc > Lembretes.Tolerancia;
        var local = l.QuandoUtc.ToLocalTime();
        string hora = local.Date == agoraUtc.ToLocalTime().Date ? local.ToString("HH:mm") : local.ToString("dd/MM HH:mm");

        bool comEmail = !string.IsNullOrWhiteSpace(l.ChaveDoEmail);
        var principal = comEmail
            ? new AvisoWindow.Botao(l.Shadow ? "Abrir no Shadow" : "Abrir o e-mail",
                                    () => { if (l.Shadow) a.AbrirShadow(); else a.AbrirEmailDoLembrete(l); }, Principal: true)
            : l.Shadow
                ? new AvisoWindow.Botao("Abrir no Shadow", a.AbrirShadow, Principal: true)
                : null;

        return (new AvisoWindow.Receita(
            AvisoWindow.Tom.Ouro, atrasado ? $"Era para {hora}" : $"Lembrete · {hora}", AvisoWindow.IconeSino,
            Fala: l.Texto,
            Sub: atrasado ? "O AIB estava fechado na hora. Entregue agora." : "",
            Shadow: l.Shadow,
            Botoes: Botoes(
                principal,
                // Adiar o que já passou da hora não faz sentido: adiar de um passado.
                atrasado ? null : new AvisoWindow.Botao("Adiar 1 h", () => a.Adiar(l)),
                new AvisoWindow.Botao("Feito", () => { }, Principal: principal == null))),
            VezDaJanela.Lembrete);
    }

    /// <summary>Por quanto tempo a resposta pronta fica antes de fechar sozinha.</summary>
    public static readonly TimeSpan PrazoDaResposta = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A resposta chegou com a janela escondida (ela se esconde sozinha ao perder o foco — então
    /// é sempre que o usuário não está olhando). Era o balão da bandeja, cortado em 200
    /// caracteres e sem botão.
    /// <para>
    /// Com erro, não fecha sozinho: um erro escondido vira uma resposta que você acha que veio.
    /// </para>
    /// </summary>
    public static (AvisoWindow.Receita Receita, VezDaJanela Vez) DePronta(
        string texto, string? erro, string nome, bool shadow, Action abrir)
    {
        var botao = new AvisoWindow.Botao(shadow ? "Abrir no Shadow" : "Abrir conversa", abrir, Principal: true);

        if (!string.IsNullOrWhiteSpace(erro))
        {
            return (new AvisoWindow.Receita(
                AvisoWindow.Tom.Perigo, "Não deu certo", AvisoWindow.IconeAlerta,
                Titulo: "A resposta parou no meio",
                Sub: erro.Length > 300 ? erro[..297] + "..." : erro,
                Shadow: shadow,
                Botoes: Botoes(botao)),
                VezDaJanela.Pronta);
        }

        return (new AvisoWindow.Receita(
            AvisoWindow.Tom.Verde, $"{nome} respondeu", AvisoWindow.IconeOk,
            Fala: string.IsNullOrWhiteSpace(texto) ? "Ação executada." : texto,
            Shadow: shadow,
            Botoes: Botoes(botao),
            FechaSozinho: PrazoDaResposta),
            VezDaJanela.Pronta);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Documentos (ferramenta doc) — mockup "Edição de documentos do AIB"
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Por quanto tempo o "Gravado" fica: o bastante para alcançar o "Desfazer".</summary>
    public static readonly TimeSpan PrazoDoGravado = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Depois de gravar planilha ou Word: abrir, abrir a pasta e — havendo cópia do original —
    /// desfazer (pedido do usuário, 19/10/2026).
    /// </summary>
    public static (AvisoWindow.Receita Receita, VezDaJanela Vez) DeGravado(
        string arquivo, bool criado, bool temCopia, Action abrir, Action abrirPasta, Action desfazer)
    {
        string nome = System.IO.Path.GetFileName(arquivo);
        return (new AvisoWindow.Receita(
            AvisoWindow.Tom.Verde, "Gravado", AvisoWindow.IconeOk,
            Titulo: criado ? $"{nome} criado" : $"{nome} atualizado",
            Sub: temCopia ? "O original ficou guardado nas cópias do AIB (as 5 últimas versões, por 3 meses)." : "",
            Botoes: Botoes(
                new AvisoWindow.Botao("Abrir", abrir, Principal: true),
                new AvisoWindow.Botao("Abrir a pasta", abrirPasta),
                temCopia ? new AvisoWindow.Botao("Desfazer", desfazer) : null),
            FechaSozinho: PrazoDoGravado),
            VezDaJanela.Pronta);
    }

    /// <summary>Os botões da escolha do arquivo travado, na ordem do índice que volta.</summary>
    public static readonly string[] EscolhasDaTrava = { "Já fechei, tentar de novo", "Salvar como cópia", "Desistir" };

    /// <summary>
    /// O arquivo está aberto no Excel ou no Word, que o travam. A janela de escolha pergunta o
    /// que fazer — o turno está parado esperando.
    /// </summary>
    public static AvisoWindow.Receita DeTrava(string arquivo, bool shadow = false)
    {
        string nome = System.IO.Path.GetFileName(arquivo);
        bool word = arquivo.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
        string programa = word ? "Word" : "Excel";
        return new AvisoWindow.Receita(
            AvisoWindow.Tom.Cuidado, "O arquivo está em uso", AvisoWindow.IconeAlerta,
            Titulo: $"Feche {(word ? "o documento" : "a planilha")} no {programa} para gravar",
            Sub: $"\"{nome}\" está aberto em outro programa, que trava o arquivo. Nada foi gravado ainda.",
            Detalhe: arquivo,
            Shadow: shadow,
            Botoes: EscolhasDaTrava.Select((t, i) => new AvisoWindow.Botao(t, () => { }, Principal: i == 0)).ToList());
    }

    private static IReadOnlyList<AvisoWindow.Botao> Botoes(params AvisoWindow.Botao?[] botoes) =>
        botoes.Where(b => b != null).Select(b => b!).ToList();
}
