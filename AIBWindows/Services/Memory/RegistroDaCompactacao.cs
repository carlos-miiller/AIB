using System;
using System.Globalization;
using System.IO;
using System.Text;
using AIB.Services.Agent;

namespace AIB.Services.Memory;

/// <summary>
/// O diário da compactação: quando ela disparou, quanto custou e o que ela tirou do prompt.
/// <para>
/// A pasta da sessão já guarda o QUE a compactação produziu — <c>raw.jsonl</c>, o cru que nunca
/// é apagado; <c>chapters.jsonl</c>; <c>acts.jsonl</c>. O que não ficava em lugar nenhum era o
/// CUSTO: quantos tokens saíram do contexto vivo, quanto tempo o resumidor levou, e —
/// principalmente — as vezes em que ela falhou. Hoje um resumo que estoura o teto de quatro
/// minutos imprime uma linha no console e morre ali; na execução seguinte ninguém sabe que a
/// conversa está andando com a poda de emergência em vez da compactação.
/// </para>
/// <para>
/// Fica ao lado dos três, na pasta da sessão, e não em <c>~/.AIB/logs</c>: um registro de
/// compactação separado da conversa que ele compactou obriga a cruzar horário à mão. Apagar a
/// pasta da sessão leva o diário junto, que é o comportamento certo.
/// </para>
/// <para>
/// A pasta e a chave são resolvidas A CADA ESCRITA, e não guardadas na construção: o
/// <c>_sessionMemory</c> troca quando o usuário zera a conversa ou restaura outra sessão, e um
/// caminho congelado escreveria o diário da conversa nova dentro da pasta da antiga.
/// </para>
/// <para>
/// NUNCA lança. Diagnóstico que derruba a compactação seria pior que a falta dele.
/// </para>
/// </summary>
public sealed class RegistroDaCompactacao
{
    public const string NomeDoArquivo = "compactacao.log";

    /// <summary>Sem BOM, como o resto da pasta da sessão.</summary>
    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<bool> _ligado;
    private readonly Func<string?> _pastaDaSessao;
    private readonly object _gate = new();

    /// <param name="ligado">A chave das configurações, lida a cada escrita.</param>
    /// <param name="pastaDaSessao">Onde o raw.jsonl da sessão CORRENTE mora.</param>
    public RegistroDaCompactacao(Func<bool> ligado, Func<string?> pastaDaSessao)
    {
        _ligado = ligado ?? throw new ArgumentNullException(nameof(ligado));
        _pastaDaSessao = pastaDaSessao ?? throw new ArgumentNullException(nameof(pastaDaSessao));
    }

    /// <summary>Onde o arquivo está agora, ou <c>null</c> se não há sessão.</summary>
    public string? Caminho
    {
        get
        {
            try
            {
                string? pasta = _pastaDaSessao();
                return string.IsNullOrWhiteSpace(pasta) ? null : Path.Combine(pasta, NomeDoArquivo);
            }
            catch { return null; }
        }
    }

    /// <summary>Se a chave está ligada agora. A tela pode tê-la mudado no meio da conversa.</summary>
    public bool Ligado
    {
        get { try { return _ligado(); } catch { return false; } }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Os eventos
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A conversa viva passou do limite. As três grandezas juntas são o que torna o gatilho
    /// conferível: sozinho, "compactando 5 turnos" não diz se foi cedo ou tarde demais.
    /// </summary>
    public void Gatilho(int vivo, int limite, int espacoDaConversa, int turnos) =>
        Escrever("GATILHO",
            $"vivo={Numero(vivo)} > limite={Numero(limite)} "
            + $"(espaço da conversa {Numero(espacoDaConversa)}), "
            + $"{turnos} turno(s) escolhido(s)");

    /// <summary>Compactação pedida pelo usuário, sem esperar o gatilho.</summary>
    public void GatilhoManual(int vivo, int espacoDaConversa, int turnos) =>
        Escrever("A PEDIDO",
            $"vivo={Numero(vivo)} (espaço da conversa {Numero(espacoDaConversa)}), "
            + $"{turnos} turno(s) escolhido(s)");

    public void Capitulo(int indice, int turnos, int primeiro, int ultimo) =>
        Escrever($"CAPÍTULO {indice}", $"{turnos} turno(s), turnos {primeiro}–{ultimo}");

    public void Ato(int indice, int capitulos, int primeiro, int ultimo) =>
        Escrever($"ATO {indice}", $"{capitulos} capítulo(s) soltos, capítulos {primeiro}–{ultimo}");

    /// <summary>
    /// O custo da chamada ao resumidor.
    /// <para>
    /// <c>prefill</c> e <c>saída</c> vêm do próprio Ollama, e não de estimativa: foi essa
    /// medição que mostrou que o raciocínio no resumo custava 226,7s dos 286,6s para produzir
    /// 117 tokens de texto. Sem o número em disco, aquela conclusão teria dependido de alguém
    /// estar com o terminal aberto na hora certa.
    /// </para>
    /// </summary>
    public void Resumo(long ms, int? prefill, int? saida, int palavras) =>
        Escrever("  · resumo",
            $"{PulsoDoTurno.Duracao(ms)}, prefill {Contagem(prefill)}, saída {Contagem(saida)}, "
            + $"{palavras} palavra(s)");

    /// <summary>
    /// O que o capítulo tirou do prompt. É o único número que torna a economia verificável
    /// depois — medido sobre as mensagens originais, antes da remoção.
    /// </summary>
    public void CapituloFechado(int tokensRemovidos, int custoDoCapitulo, int artefatos, int vivoAgora) =>
        Escrever("  · fechado",
            $"{Numero(tokensRemovidos)} → {Numero(custoDoCapitulo)} token(s) "
            + $"(economia {Numero(tokensRemovidos - custoDoCapitulo)}), {artefatos} artefato(s), "
            + $"vivo agora {Numero(vivoAgora)}");

    public void AtoFechado(int dosCapitulos, int custoDoAto, int artefatos, int fatosNovos) =>
        Escrever("  · fechado",
            $"{Numero(dosCapitulos)} → {Numero(custoDoAto)} token(s) na promoção "
            + $"(economia {Numero(dosCapitulos - custoDoAto)}), {artefatos} artefato(s), "
            + $"{fatosNovos} fato(s) durável(is) novo(s)");

    /// <summary>
    /// A razão de este arquivo existir. Uma compactação que falha some no console e a conversa
    /// segue na poda de emergência sem ninguém saber.
    /// </summary>
    public void Falhou(string oQue, string porque) =>
        Escrever("FALHOU", $"{oQue}: {porque}");

    /// <summary>Nada a fazer — registrado porque "não compactou" tem causas diferentes.</summary>
    public void Pulou(string porque) => Escrever("PULOU", porque);

    /// <summary>
    /// A poda de emergência: o corte cego que age quando a compactação não deu conta.
    /// <para>
    /// Não é compactação, mas é o que acontece NO LUGAR dela, e por isso mora no mesmo diário.
    /// A poda descarta sem substituto — o que ela come não vira capítulo, não vira artefato e
    /// não vira nada: some. Uma conversa que anda na poda em vez de na compactação está
    /// perdendo material de verdade, e até aqui isso não deixava rastro nenhum.
    /// </para>
    /// </summary>
    public void Podou(int mensagens, int antes, int depois, int teto) =>
        Escrever("PODA",
            $"{mensagens} mensagem(ns) cortada(s) sem substituto, "
            + $"{Numero(antes)} → {Numero(depois)} token(s) (teto {Numero(teto)})");

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uma linha, com hora local. O cabeçalho nasce junto do arquivo: quem abre o diário no
    /// meio de uma investigação precisa saber com que modelo e com que gatilho aquilo rodou.
    /// </summary>
    private void Escrever(string rotulo, string texto)
    {
        if (!Ligado) return;

        string? caminho = Caminho;
        if (caminho == null) return;

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);

                if (!File.Exists(caminho) || new FileInfo(caminho).Length == 0)
                    File.AppendAllText(caminho, Cabecalho(), SemBom);

                string hora = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(caminho,
                    $"{hora}  {rotulo,-14}{texto}{Environment.NewLine}", SemBom);
            }
        }
        catch (Exception ex)
        {
            // Só o console: chamar Escrever de novo aqui daria um laço.
            Console.WriteLine($"[MEMORIA] não consegui gravar o diário da compactação: {ex.Message}");
        }
    }

    private static string Cabecalho()
    {
        var texto = new StringBuilder();
        texto.Append("═══ diário da compactação ═══").Append(Environment.NewLine);
        texto.Append("Aberto em ")
             .Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))
             .Append(Environment.NewLine);
        texto.Append("Os resumos ficam em chapters.jsonl e acts.jsonl; aqui fica o CUSTO deles.")
             .Append(Environment.NewLine);
        texto.Append(Environment.NewLine);
        return texto.ToString();
    }

    /// <summary>Milhar separado: 12.408 se lê, 12408 se conta.</summary>
    private static string Numero(int n) => n.ToString("N0", new CultureInfo("pt-BR"));

    /// <summary>
    /// Contagem que o provedor pode não ter mandado. O OpenAI não devolve os mesmos campos do
    /// Ollama, e escrever "0" onde o número não existe inventaria uma medição.
    /// </summary>
    private static string Contagem(int? n) => n.HasValue ? Numero(n.Value) + " tok" : "n/d";
}
