using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIB.Services.Mail;

/// <summary>
/// Uma entrada no histórico de uma conversa: o que aconteceu nela, e quando.
/// </summary>
/// <param name="Em">Instante da mensagem, em UTC ISO-8601.</param>
/// <param name="Uid">UID IMAP da mensagem. É o que impede contar a mesma duas vezes.</param>
/// <param name="De">Remetente, como veio do servidor.</param>
/// <param name="Assunto">Assunto da mensagem.</param>
/// <param name="Urgencia">Veredito da triagem. Vazio quando a mensagem é sua.</param>
/// <param name="Resumo">
/// Resumo gerado pelo modelo. Vazio quando a mensagem é sua — não há o que triar no que você
/// mesmo escreveu.
/// </param>
/// <param name="Minha">
/// Veio da pasta de Enviados: foi VOCÊ quem escreveu. É o que vira a vez da conversa.
/// </param>
/// <param name="Origem"><c>vigia</c>, <c>enviados</c> ou <c>recarregar</c>.</param>
public sealed record EntradaDaConversa(
    string Em,
    uint Uid,
    string De,
    string Assunto,
    string Urgencia = "",
    string Resumo = "",
    bool Minha = false,
    string Origem = "vigia");

/// <summary>
/// O histórico de cada conversa de e-mail, em <c>~/.AIB/email/conversas/{hash}/triagem.jsonl</c>.
/// <para>
/// Existe porque o agrupamento por lote só enxerga UMA passada. A thread triada ontem que
/// recebe resposta hoje chegaria como "1 mensagem" — os campos de
/// <c>tela-chat-v3.html §3.10</c> mentiriam a partir da segunda passada. O arquivo é o que
/// torna "3 respostas" verdadeiro.
/// </para>
/// <para>
/// O CORPO DA MENSAGEM NUNCA ENTRA AQUI, e a garantia é estrutural: <see cref="EntradaDaConversa"/>
/// não tem campo para ele. O que se grava é VEREDITO — remetente, assunto, urgência, resumo —
/// exatamente o que a regra 3, como foi estreitada, permite persistir.
/// </para>
/// <para>
/// APAGÁVEL, ao contrário do <c>raw.jsonl</c> da conversa com a IA. Aquele nunca é apagado
/// porque é a SUA conversa; esta é de terceiros, e um arquivo permanente de quem te escreveu e
/// sobre o quê é outra coisa. Obedece <c>MailJournalDays</c>, e zero varre tudo. Ninguém pode
/// aplicar a regra do chat aqui por analogia.
/// </para>
/// <para>
/// O nome da pasta é um HASH, e não o assunto: assunto em nome de diretório vaza conteúdo para
/// qualquer listagem, backup ou indexador, sem ninguém abrir arquivo nenhum. O assunto fica
/// DENTRO do JSON, onde a regra 6 pede que esteja.
/// </para>
/// </summary>
public sealed class ArquivoDeConversas
{
    /// <summary>Alinhado com <see cref="DiarioDeTriagem.DiasMantidos"/>.</summary>
    public const int DiasMantidos = DiarioDeTriagem.DiasMantidos;

    public const string NomeDoArquivo = "triagem.jsonl";

    /// <summary>A chave em claro dentro da pasta. Ver o comentário em <see cref="Anotar"/>.</summary>
    public const string NomeDaChave = "chave.txt";

    /// <summary>Sem BOM, como o resto dos arquivos de linha do projeto.</summary>
    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _porta = new();
    private readonly string _pasta;

    public ArquivoDeConversas(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _pasta = Path.Combine(baseDir, "email", "conversas");
    }

    public string Pasta => _pasta;

    /// <summary>
    /// A chave de uma conversa: conta + thread, ou conta + uid quando o provedor não expõe
    /// thread. Sem a conta, a mesma thread em duas caixas viraria um arquivo só.
    /// </summary>
    public static string Chave(string? conta, string? threadId, uint uid) =>
        string.IsNullOrWhiteSpace(threadId)
            ? $"{conta}|uid:{uid}"
            : $"{conta}|thr:{threadId}";

    /// <summary>
    /// Nome de pasta a partir da chave: SHA-256 em hex, 32 caracteres.
    /// <para>
    /// Hash e não assunto — ver a nota da classe. Metade do SHA-256 é folgado para a ordem de
    /// grandeza aqui (milhares de conversas), e mantém o nome legível numa linha.
    /// </para>
    /// </summary>
    public static string NomeDaPasta(string chave)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(chave ?? ""));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    public string CaminhoDaConversa(string chave) =>
        Path.Combine(_pasta, NomeDaPasta(chave), NomeDoArquivo);

    // ─────────────────────────────────────────────────────────────────────────
    // Escrita
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acrescenta uma entrada. Append-only: o arquivo é HISTÓRICO, e sobrescrever apagaria
    /// justamente a mudança de ideia que a regra 6 pede poder conferir.
    /// <para>
    /// A mesma UID pela mesma origem não entra duas vezes: uma passada que relê a janela inteira
    /// veria a mesma mensagem de novo e dobraria a contagem de respostas. Recarregar, sim,
    /// acrescenta — ali o veredito pode ter mudado, e é o ponto.
    /// </para>
    /// <para>
    /// Nunca lança. Registro é acréscimo, e um acréscimo que derruba a triagem não vale.
    /// </para>
    /// </summary>
    public bool Anotar(string chave, EntradaDaConversa entrada, int diasMantidos = DiasMantidos)
    {
        if (entrada == null || string.IsNullOrWhiteSpace(chave)) return false;

        if (diasMantidos <= 0)
        {
            Apagar();
            return false;
        }

        try
        {
            string caminho = CaminhoDaConversa(chave);

            lock (_porta)
            {
                if (entrada.Origem != "recarregar" && JaTem(caminho, entrada))
                    return false;

                string pastaDaConversa = Path.GetDirectoryName(caminho)!;
                Directory.CreateDirectory(pastaDaConversa);

                // A chave em claro, ao lado do histórico. O nome da pasta é hash e não se
                // desfaz: sem este arquivo não haveria como devolver a lista de threads que
                // têm histórico — e é ela que as mantém VIGIADAS, para uma resposta não ser
                // descartada pelo funil e deixar o arquivo envelhecer em silêncio.
                //
                // A chave é conta + id de thread. Não é conteúdo: nenhum assunto, nenhum
                // remetente, nada que uma listagem de diretório revele.
                string arquivoDaChave = Path.Combine(pastaDaConversa, NomeDaChave);
                if (!File.Exists(arquivoDaChave)) File.WriteAllText(arquivoDaChave, chave, SemBom);

                File.AppendAllText(
                    caminho, JsonSerializer.Serialize(entrada, Json) + Environment.NewLine, SemBom);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui anotar a conversa: {ex.Message}");
            return false;
        }
    }

    private bool JaTem(string caminho, EntradaDaConversa nova)
    {
        string id = Identidade(nova);

        foreach (var e in LerArquivo(caminho))
            if (string.Equals(Identidade(e), id, StringComparison.Ordinal)
                && string.Equals(e.Origem, nova.Origem, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// O que faz uma entrada ser a MESMA mensagem.
    /// <para>
    /// Normalmente é a UID. A pasta de Enviados, porém, não devolve uid nenhuma — só a thread e
    /// o instante — e ali todas as entradas teriam uid 0. Sem esta distinção, a SEGUNDA resposta
    /// sua na mesma conversa seria tratada como repetição da primeira: não entraria no arquivo,
    /// e a data da conversa pararia no seu primeiro envio.
    /// </para>
    /// </summary>
    public static string Identidade(EntradaDaConversa e) =>
        e.Uid != 0 ? "u" + e.Uid : "t" + (e.Em ?? "");

    // ─────────────────────────────────────────────────────────────────────────
    // Leitura
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>O histórico de uma conversa, na ordem em que foi escrito. Vazio se não existe.</summary>
    public IReadOnlyList<EntradaDaConversa> Ler(string chave) =>
        string.IsNullOrWhiteSpace(chave)
            ? Array.Empty<EntradaDaConversa>()
            : LerArquivo(CaminhoDaConversa(chave));

    private static IReadOnlyList<EntradaDaConversa> LerArquivo(string caminho)
    {
        var itens = new List<EntradaDaConversa>();
        if (!File.Exists(caminho)) return itens;

        try
        {
            foreach (string linha in File.ReadLines(caminho, SemBom))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;

                try
                {
                    var item = JsonSerializer.Deserialize<EntradaDaConversa>(linha, Json);
                    if (item != null) itens.Add(item);
                }
                catch (JsonException)
                {
                    // Linha truncada por queda no meio de uma escrita. Perder o arquivo inteiro
                    // por causa dela seria o pior desfecho — mesma política do raw.jsonl.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui ler a conversa: {ex.Message}");
        }

        return itens;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // O estado que a lista mostra
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O estado de uma conversa, derivado do histórico inteiro — nunca do lote da passada.
    /// </summary>
    /// <param name="Mensagens">UIDs distintas conhecidas.</param>
    /// <param name="UltimaEm">Instante da mensagem mais recente, em UTC.</param>
    /// <param name="EsperandoVoce">A última palavra é do outro lado.</param>
    /// <param name="Urgencia">
    /// O último veredito de uma mensagem que NÃO é sua. Responder não apaga a urgência do que
    /// se está respondendo.
    /// </param>
    /// <param name="Resumo">O resumo que acompanha esse veredito.</param>
    /// <param name="Assunto">Assunto da mensagem mais recente.</param>
    public sealed record Estado(
        int Mensagens,
        DateTime UltimaEm,
        bool EsperandoVoce,
        string Urgencia,
        string Resumo,
        string Assunto);

    /// <summary>
    /// Lê o histórico e devolve o que a linha de §3.10 mostra. <c>null</c> quando a conversa
    /// não tem arquivo — aí ela é nova e o lote da passada já basta.
    /// </summary>
    public Estado? EstadoDe(string chave)
    {
        var entradas = Ler(chave);
        return entradas.Count == 0 ? null : Resumir(entradas);
    }

    /// <summary>Função pura: o estado a partir das entradas. Existe separada para ser testável.</summary>
    public static Estado Resumir(IReadOnlyList<EntradaDaConversa> entradas)
    {
        var ordenadas = entradas.OrderBy(e => DiarioDeTriagem.Quando(e.Em)).ToList();
        var ultima = ordenadas[^1];

        // Por IDENTIDADE, e não por linha: recarregar acrescenta uma entrada nova para uma
        // mensagem que já estava lá, e contá-la de novo inflaria "N respostas" a cada clique no
        // botão. Ver Identidade — as respostas SUAS não têm uid e se distinguem pelo carimbo.
        int mensagens = ordenadas.Select(Identidade).Distinct(StringComparer.Ordinal).Count();

        // O último veredito de quem NÃO é você. Se a mais recente é sua, a conversa mantém a
        // urgência daquilo que você está respondendo e só vira "Aguardando retorno" — senão
        // responder apagaria o motivo de ela existir.
        var comVeredito = ordenadas.LastOrDefault(
            e => !e.Minha && !string.IsNullOrWhiteSpace(e.Urgencia));

        return new Estado(
            mensagens,
            DiarioDeTriagem.Quando(ultima.Em),
            EsperandoVoce: !ultima.Minha,
            Urgencia: comVeredito?.Urgencia ?? "",
            Resumo: comVeredito?.Resumo ?? "",
            Assunto: ultima.Assunto ?? "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Retenção
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apaga as conversas sem movimento há mais de <paramref name="diasMantidos"/> dias.
    /// <para>
    /// Pela data da ÚLTIMA entrada, e não pela data do arquivo: copiar a pasta de dados
    /// atualizaria o carimbo do sistema de arquivos e ressuscitaria conversas de meses atrás.
    /// </para>
    /// </summary>
    public void Limpar(int diasMantidos = DiasMantidos, DateTime? agoraUtc = null)
    {
        if (diasMantidos <= 0) { Apagar(); return; }

        try
        {
            if (!Directory.Exists(_pasta)) return;

            DateTime corte = (agoraUtc ?? DateTime.UtcNow).AddDays(-diasMantidos);

            foreach (string dir in Directory.GetDirectories(_pasta))
            {
                try
                {
                    var entradas = LerArquivo(Path.Combine(dir, NomeDoArquivo));

                    // Pasta sem arquivo legível não tem o que preservar.
                    DateTime ultima = entradas.Count == 0
                        ? DateTime.MinValue
                        : entradas.Max(e => DiarioDeTriagem.Quando(e.Em));

                    if (ultima < corte) Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui limpar as conversas: {ex.Message}");
        }
    }

    /// <summary>
    /// Varre tudo. É o que <c>MailJournalDays = 0</c> significa: a regra 3 estrita de volta,
    /// sem nada de e-mail em disco.
    /// </summary>
    public void Apagar()
    {
        try
        {
            if (Directory.Exists(_pasta)) Directory.Delete(_pasta, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui apagar as conversas: {ex.Message}");
        }
    }

    /// <summary>
    /// As threads que têm histórico, por conta.
    /// <para>
    /// Ter arquivo passa a ser motivo de VIGIA: sem isso, a resposta a uma conversa já triada
    /// podia ser descartada pelo funil — como mala-direta, envio em massa, remetente
    /// desconhecido — e o histórico ficaria parado sem ninguém notar. A porta de fuga por
    /// conversa vigiada já existia no <see cref="FiltroDeTriagem"/>; aqui ela ganha uma fonte a
    /// mais.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ThreadsComHistorico()
    {
        var saida = new List<string>();

        try
        {
            if (!Directory.Exists(_pasta)) return saida;

            foreach (string dir in Directory.GetDirectories(_pasta))
            {
                try
                {
                    string arquivo = Path.Combine(dir, NomeDaChave);
                    if (!File.Exists(arquivo)) continue;

                    string chave = File.ReadAllText(arquivo, SemBom).Trim();
                    int marca = chave.IndexOf("|thr:", StringComparison.Ordinal);

                    // Conversa sem thread (provedor que não expõe X-GM-THRID) não tem o que
                    // vigiar: cada mensagem é a própria conversa e não existe "a resposta".
                    if (marca < 0) continue;

                    string thrid = chave[(marca + 5)..];
                    if (thrid.Length > 0) saida.Add(thrid);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui listar as conversas: {ex.Message}");
        }

        return saida;
    }

    /// <summary>Quantas conversas existem em disco. Diagnóstico e tela.</summary>
    public int Quantas
    {
        get
        {
            try { return Directory.Exists(_pasta) ? Directory.GetDirectories(_pasta).Length : 0; }
            catch { return 0; }
        }
    }

    /// <summary>Instante em ISO-8601 UTC, como as entradas guardam.</summary>
    public static string Agora(DateTime instanteUtc) =>
        instanteUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
