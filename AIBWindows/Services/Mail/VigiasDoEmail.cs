using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIB.Services.Mail;

/// <summary>
/// Uma conversa sob vigilância — o usuário respondeu ali e provavelmente espera retorno.
/// <para>
/// A comparação é <c>thrid == thrid</c>: exata, instantânea, grátis. É de propósito que o canal
/// entre o modelo e o código seja ESTADO ESTRUTURADO, e não prosa a ser interpretada. Instrução
/// sutil em linguagem natural seria aplicada de um jeito hoje e de outro amanhã — um vigia que
/// às vezes vigia.
/// </para>
/// </summary>
public sealed class VigiaDeThread
{
    /// <summary>X-GM-THRID da conversa.</summary>
    public string Thrid { get; set; } = "";

    /// <summary>Por que esta conversa está sendo vigiada. Legível, para o arquivo ser auditável.</summary>
    public string Porque { get; set; } = "";

    /// <summary>
    /// Até quando. Passou a data, ela deixa de valer para o funil, mas CONTINUA na lista
    /// "Aguardando retorno", em vermelho, até você decidir (+7 dias ou dispensar). Sumir em
    /// silêncio é o que a regra 6 do vigia proíbe.
    /// </summary>
    public DateTime Ate { get; set; }

    /// <summary>
    /// Você mandou parar ("Parar de vigiar" ou "Dispensar"). Fica gravada para a passada
    /// seguinte não a ressuscitar a partir da mesma mensagem na pasta de enviados; uma
    /// mensagem sua NOVA na conversa abre outra vigia.
    /// </summary>
    public bool Parada { get; set; }

    // Houve aqui um "acorda9b": vigiar esta conversa acordaria o modelo na hora, em vez de
    // esperar o digest. Nunca acordou ninguém — quem escreve as vigias é o Atualizar, e ele
    // gravava sempre falso. O campo saiu em vez de ganhar a interface que faltava: o que
    // justifica interromper é a RAJADA, não toda resposta que chega, e um mostrador para
    // interromper por resposta seria vender a interrupção como se ela fosse barata.
    //
    // O arquivo de quem já tem o campo continua abrindo: o leitor de JSON ignora propriedade
    // que a classe não tem, e é assim de propósito.

    /// <summary>Quando o usuário escreveu naquela conversa pela última vez.</summary>
    public DateTime RespondidaEm { get; set; }

    public bool Vencida(DateTime agoraUtc) => agoraUtc.Date > Ate.Date;

    /// <summary>Vale para o funil: nem parada, nem vencida.</summary>
    public bool Ativa(DateTime agoraUtc) => !Parada && !Vencida(agoraUtc);
}

/// <summary>
/// O <c>vigias.json</c> de <c>~/.AIB/email/</c>.
/// <para>
/// Guarda as conversas em que o usuário escreveu e ainda espera retorno. Detectar isso é IMAP
/// puro — mensagem em <c>[Gmail]/Sent</c> com a mesma <c>X-GM-THRID</c> — e por isso não passa
/// por modelo nenhum: <b>a decisão de interromper o usuário fica inteira na parte que não
/// alucina.</b>
/// </para>
/// <para>
/// O ciclo fecha sozinho: o código escreve as vigias, o código as aplica, e cada uma expira na
/// data marcada. Dá para abrir o arquivo e ver por que houve interrupção.
/// </para>
/// <para>
/// NENHUM CORPO DE MENSAGEM AQUI. Entram identificador de conversa, uma frase de motivo e duas
/// datas — regra 3 do vigia aplicada ao único arquivo desta feature que persiste.
/// </para>
/// </summary>
public sealed class VigiasDoEmail
{
    /// <summary>
    /// Por quantos dias uma conversa respondida continua vigiada.
    /// <para>
    /// Quem responde geralmente espera retorno, mas não para sempre: sem prazo, a lista só
    /// cresce e em um mês toda a caixa estaria vigiada — que é o mesmo que nenhuma estar.
    /// </para>
    /// </summary>
    public const int DiasDeVigia = 7;

    private static readonly JsonSerializerOptions Formato = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _caminho;

    public VigiasDoEmail(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _caminho = Path.Combine(baseDir, "email", "vigias.json");
    }

    public string Caminho => _caminho;

    /// <summary>"+7 dias": estende o prazo, contado de hoje se ela já venceu. Devolve false se não achou.</summary>
    public bool Estender(string thrid, int dias, DateTime agoraUtc) =>
        Mudar(thrid, v =>
        {
            v.Ate = (v.Vencida(agoraUtc) ? agoraUtc : v.Ate).AddDays(dias);
            v.Porque += $"; estendida em {agoraUtc.ToLocalTime():dd/MM}";
        });

    /// <summary>"Parar de vigiar" e "Dispensar": sai da lista e do funil. Devolve false se não achou.</summary>
    public bool Parar(string thrid) =>
        Mudar(thrid, v =>
        {
            v.Parada = true;
            v.Porque += "; parada por você";
        });

    private bool Mudar(string thrid, Action<VigiaDeThread> mudanca)
    {
        var vigias = Ler().ToList();
        var alvo = vigias.FirstOrDefault(v => string.Equals(v.Thrid, thrid, StringComparison.Ordinal));
        if (alvo == null) return false;

        mudanca(alvo);
        Gravar(vigias);
        return true;
    }

    public IReadOnlyList<VigiaDeThread> Ler()
    {
        try
        {
            if (!File.Exists(_caminho)) return Array.Empty<VigiaDeThread>();

            var lidas = JsonSerializer.Deserialize<List<VigiaDeThread>>(
                File.ReadAllText(_caminho), Formato);

            return lidas ?? new List<VigiaDeThread>();
        }
        catch (Exception ex)
        {
            // Arquivo corrompido não pode calar o vigia: sem vigias ele ainda tria e ainda
            // entrega o digest, só deixa de saber quais conversas esperam retorno.
            Console.WriteLine($"[VIGIA] vigias.json ilegível ({ex.Message}); seguindo sem vigias.");
            return Array.Empty<VigiaDeThread>();
        }
    }

    public void Gravar(IEnumerable<VigiaDeThread> vigias)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_caminho)!);
            File.WriteAllText(_caminho, JsonSerializer.Serialize(vigias.ToList(), Formato));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIGIA] não consegui gravar o vigias.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Junta as conversas recém-respondidas às que já estavam vigiadas.
    /// <para>
    /// Função pura sobre o que entra, para poder ser testada sem disco nem relógio da máquina.
    /// Uma mensagem sua MAIS NOVA que a vigiada RENOVA o prazo em vez de duplicá-la: continuar
    /// respondendo é continuar esperando retorno. A MESMA mensagem, relida da pasta de enviados
    /// a cada passada, não mexe em nada — antes ela regravava a vigia, e isso desfaria o "+7
    /// dias" e o "Parar de vigiar" na passada seguinte.
    /// </para>
    /// <para>
    /// A VENCIDA FICA: ela é a conversa que ficou sem resposta, e é exatamente o que a lista
    /// "Aguardando retorno" mostra em vermelho até você decidir. Sai a PARADA cuja mensagem já
    /// passou da janela da pasta de enviados — dali em diante não há o que a ressuscite, e a
    /// marca não tem mais o que impedir.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VigiaDeThread> Atualizar(
        IEnumerable<VigiaDeThread> existentes,
        IEnumerable<ThreadRespondida> respondidas,
        DateTime agoraUtc)
    {
        var porThread = new Dictionary<string, VigiaDeThread>(StringComparer.Ordinal);

        foreach (var v in existentes ?? Array.Empty<VigiaDeThread>())
        {
            if (string.IsNullOrWhiteSpace(v.Thrid)) continue;
            if (v.Parada && v.RespondidaEm < agoraUtc.AddDays(-(DiasDeVigia + 1))) continue;
            porThread[v.Thrid] = v;
        }

        foreach (var r in respondidas ?? Array.Empty<ThreadRespondida>())
        {
            if (string.IsNullOrWhiteSpace(r.Thrid)) continue;
            if (porThread.TryGetValue(r.Thrid, out var ja) && r.QuandoUtc <= ja.RespondidaEm) continue;

            porThread[r.Thrid] = new VigiaDeThread
            {
                Thrid = r.Thrid,
                Porque = $"você respondeu em {r.QuandoUtc.ToLocalTime():dd/MM}; aguarda retorno",
                RespondidaEm = r.QuandoUtc,
                Ate = r.QuandoUtc.AddDays(DiasDeVigia)
            };
        }

        return porThread.Values.OrderByDescending(v => v.RespondidaEm).ToList();
    }
}

/// <summary>Uma conversa em que o usuário escreveu, vinda de <c>[Gmail]/Sent</c>.</summary>
/// <param name="Thrid">X-GM-THRID.</param>
/// <param name="QuandoUtc">Quando ele escreveu.</param>
/// <param name="Assunto">Do envelope. Sem ele a tela não tem como nomear a conversa que espera retorno.</param>
/// <param name="Para">O primeiro destinatário, com "+N" quando há mais.</param>
/// <param name="Abriu">
/// Esta mensagem COMEÇOU a conversa: no Gmail, o X-GM-THRID é o X-GM-MSGID da primeira
/// mensagem da thread. É o que dá o selo "Você iniciou".
/// </param>
public readonly record struct ThreadRespondida(
    string Thrid, DateTime QuandoUtc, string Assunto = "", string Para = "", bool Abriu = false);
