using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AIB.Services.Mail;

/// <summary>Uma mensagem que passou pelo degrau 3 e ganhou veredito.</summary>
/// <param name="Remetente">Endereço de quem mandou, minúsculo.</param>
/// <param name="Nome">Nome do remetente como veio no envelope.</param>
/// <param name="Assunto">Assunto, como veio.</param>
/// <param name="Resumo">O que estão pedindo — a frase do modelo, ou a de código quando ele falhou.</param>
/// <param name="Urgencia">"maxima", "media" ou "baixa".</param>
/// <param name="Conta">A caixa em que a mensagem chegou.</param>
/// <param name="RecebidaUtc">Quando o servidor a recebeu, em ISO 8601.</param>
public sealed record EmailTriado(
    string Remetente,
    string Nome,
    string Assunto,
    string Resumo,
    string Urgencia,
    string Conta,
    string RecebidaUtc);

/// <summary>Uma passada do vigia, como ela ficou registrada.</summary>
/// <param name="QuandoUtc">Fim da passada, em ISO 8601.</param>
/// <param name="Lidas">Quantas mensagens saíram do servidor.</param>
/// <param name="Descartadas">Quantas o funil derrubou antes do modelo.</param>
/// <param name="Triados">
/// As que chegaram ao modelo, TODAS elas — inclusive as de urgência baixa, que não vão para a
/// tela. A tela mostra o que pede ação; a pergunta "quantos foram tratados hoje" precisa do
/// número inteiro.
/// </param>
public sealed record PassadaAnotada(
    string QuandoUtc,
    int Lidas,
    int Descartadas,
    IReadOnlyList<EmailTriado> Triados)
{
    public int Urgentes => Triados?.Count(t => Ehmaxima(t.Urgencia)) ?? 0;

    private static bool Ehmaxima(string? u) =>
        string.Equals(u, "maxima", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// O que a triagem automática já fez, dia a dia, em <c>~/.AIB/email/diario/</c>.
/// <para>
/// Existe para uma coisa só: dar à conversa visibilidade do trabalho que o vigia fez sozinho.
/// Sem isto, "quantos e-mails foram tratados hoje" não tem resposta — o
/// <see cref="MailDigestService.Ultimo"/> guarda só a ÚLTIMA passada e morre com o programa,
/// então o digest das 8h25 já não existe ao meio-dia.
/// </para>
/// <para>
/// ONDE ISTO AFROUXA A REGRA 3. A regra dizia que conteúdo de e-mail não cria raiz em disco.
/// Aqui gravam-se remetente, assunto e o resumo de uma frase — os mesmos campos que já apareciam
/// na tela do Shadow e no painel. O que continua sem NUNCA tocar o disco é o CORPO da mensagem:
/// ele vive na memória durante uma triagem e morre no fim dela. A troca é deliberada e tem
/// mostrador: <c>MailJournalDays = 0</c> desliga o diário e devolve a regra 3 estrita, ao preço
/// de a conversa não saber mais nada sobre a caixa.
/// </para>
/// <para>
/// O QUE O ZERO NÃO COBRE: o e-mail aberto no chat com "Abrir com". A primeira fala daquela
/// conversa é o enquadramento — remetente, assunto, data, urgência e resumo — e ele é gravado
/// no <c>raw.jsonl</c> (que nunca é apagado) e no <c>chat_history.json</c> como qualquer fala,
/// com qualquer <c>MailJournalDays</c>. O corpo continua fora (<see cref="ConteudoDeTerceiros"/>).
/// É uma exceção conhecida e ainda sem decisão; ver <c>ChatWindow.EnquadramentoDoEmail</c>.
/// </para>
/// <para>
/// Um arquivo por dia, e não um só: apagar o de anteontem é apagar um arquivo, e ler "hoje" não
/// obriga a carregar a semana. O <c>raw.jsonl</c> nunca é apagado; este não é ele.
/// </para>
/// </summary>
public sealed class DiarioDeTriagem
{
    /// <summary>Quantos dias ficam. Alinhado com <see cref="VigiasDoEmail.DiasDeVigia"/>.</summary>
    public const int DiasMantidos = 7;

    /// <summary>
    /// Indentado e SEM escapar acento. O diário é um arquivo que uma pessoa abre — para
    /// conferir o que a triagem decidiu, que é a regra 6. Um assunto virado em sequência de
    /// escapes hexadecimais cumpre o formato e falha o propósito.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _porta = new();
    private readonly string _pasta;

    public DiarioDeTriagem(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _pasta = Path.Combine(baseDir, "email", "diario");
    }

    public string Pasta => _pasta;

    /// <summary>
    /// O arquivo de um dia. Nome por DATA LOCAL, e não UTC: quem pergunta "hoje" está pensando
    /// no calendário da parede, e um digest das 22h no Brasil já é o dia seguinte em UTC.
    /// </summary>
    public string CaminhoDoDia(DateTime diaLocal) =>
        Path.Combine(_pasta, $"diario-{diaLocal:yyyy-MM-dd}.json");

    /// <summary>
    /// Anota uma passada. Falha de disco não derruba a triagem: o diário é acréscimo, e um
    /// acréscimo que quebra o principal não vale.
    /// </summary>
    public void Gravar(PassadaAnotada passada, int diasMantidos = DiasMantidos)
    {
        // Zero não é "pare de gravar": é "não quero isto em disco". Deixar para trás o que foi
        // gravado ontem faria o mostrador prometer uma coisa e entregar outra — e o que ficasse
        // continuaria consultável pela ferramenta.
        if (diasMantidos <= 0)
        {
            Apagar();
            return;
        }

        if (passada == null) return;

        try
        {
            lock (_porta)
            {
                Directory.CreateDirectory(_pasta);

                DateTime dia = Quando(passada.QuandoUtc).ToLocalTime();
                string caminho = CaminhoDoDia(dia);

                var doDia = LerArquivo(caminho).ToList();
                doDia.Add(passada);

                File.WriteAllText(caminho, JsonSerializer.Serialize(doDia, Json));
            }

            Limpar(diasMantidos);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui anotar a passada no diário: {ex.Message}");
        }
    }

    /// <summary>As passadas de um dia, na ordem em que aconteceram.</summary>
    public IReadOnlyList<PassadaAnotada> Ler(DateTime diaLocal) => LerArquivo(CaminhoDoDia(diaLocal));

    /// <summary>
    /// As passadas de um intervalo de dias, inclusive nas duas pontas. Dia sem arquivo
    /// simplesmente não contribui — ausência de triagem não é erro.
    /// </summary>
    public IReadOnlyList<PassadaAnotada> LerPeriodo(DateTime deLocal, DateTime ateLocal)
    {
        var tudo = new List<PassadaAnotada>();

        for (var d = deLocal.Date; d <= ateLocal.Date; d = d.AddDays(1))
            tudo.AddRange(Ler(d));

        return tudo.OrderBy(p => Quando(p.QuandoUtc)).ToList();
    }

    /// <summary>
    /// Apaga os dias antigos. Só mexe em arquivos com o prefixo desta feature.
    /// <para>
    /// Corta por DATA, e não por contagem. A versão anterior mantinha os N arquivos mais
    /// recentes: quem abre o AIB dois dias por semana ficava com um mês e meio de diário sob
    /// um mostrador que diz "dias", e quem o abre todo dia perdia o de ontem se uma passada
    /// tivesse falhado no meio. O mostrador promete dias; o corte tem de ser em dias.
    /// </para>
    /// <para>
    /// A data sai do NOME do arquivo, não do carimbo do sistema de arquivos: copiar a pasta de
    /// dados atualiza o carimbo e ressuscitaria o diário de meses atrás.
    /// </para>
    /// </summary>
    /// <param name="hojeLocal">O dia de referência. Existe para o ensaio não depender do relógio.</param>
    public void Limpar(int diasMantidos = DiasMantidos, DateTime? hojeLocal = null)
    {
        if (diasMantidos <= 0) { Apagar(); return; }

        try
        {
            if (!Directory.Exists(_pasta)) return;

            // Inclusivo nas duas pontas: "7 dias" é hoje mais os seis anteriores, e não hoje
            // mais sete — senão o mostrador entrega sempre um dia a mais do que promete.
            DateTime corte = (hojeLocal ?? DateTime.Now).Date.AddDays(-(diasMantidos - 1));

            foreach (string arquivo in Directory.GetFiles(_pasta, "diario-*.json"))
            {
                // Nome fora do padrão FICA. Não sabemos de que dia ele é, e apagar por não
                // entender é a diferença entre uma poda e uma varredura.
                if (!DiaDoArquivo(arquivo, out var dia)) continue;
                if (dia >= corte) continue;

                try { File.Delete(arquivo); } catch { }
            }
        }
        catch
        {
            // Não conseguir limpar não é motivo para não anotar.
        }
    }

    /// <summary>
    /// O dia que o nome do arquivo carrega. Público porque é o que liga o mostrador em dias ao
    /// que existe em disco, e é isso que o ensaio confere.
    /// </summary>
    public static bool DiaDoArquivo(string caminho, out DateTime diaLocal)
    {
        diaLocal = default;

        string nome = Path.GetFileNameWithoutExtension(caminho);
        if (!nome.StartsWith("diario-", StringComparison.Ordinal)) return false;

        return DateTime.TryParseExact(nome.Substring("diario-".Length), "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None,
                                      out diaLocal);
    }

    /// <summary>Apaga o diário inteiro. É o que o mostrador em zero faz.</summary>
    public void Apagar()
    {
        try
        {
            if (Directory.Exists(_pasta)) Directory.Delete(_pasta, recursive: true);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<PassadaAnotada> LerArquivo(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return Array.Empty<PassadaAnotada>();

            return JsonSerializer.Deserialize<List<PassadaAnotada>>(File.ReadAllText(caminho))
                   ?? (IReadOnlyList<PassadaAnotada>)Array.Empty<PassadaAnotada>();
        }
        catch
        {
            // Arquivo corrompido não pode calar o dia inteiro nem derrubar a leitura.
            return Array.Empty<PassadaAnotada>();
        }
    }

    /// <summary>Lê o carimbo ISO. Carimbo ilegível vira "agora", que é melhor que estourar.</summary>
    public static DateTime Quando(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                          DateTimeStyles.RoundtripKind, out var q)
            ? q
            : DateTime.UtcNow;
}
