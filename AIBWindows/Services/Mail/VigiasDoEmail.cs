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

    /// <summary>Até quando. Passou a data, a vigia morre sozinha.</summary>
    public DateTime Ate { get; set; }

    /// <summary>
    /// Se uma mensagem nova nesta conversa acorda o modelo NA HORA, em vez de esperar o digest.
    /// <para>
    /// Falso por padrão. Interromper é caro, e o que justifica interromper é a rajada, não toda
    /// resposta que chega.
    /// </para>
    /// </summary>
    public bool Acorda9b { get; set; }

    /// <summary>Quando o usuário escreveu naquela conversa pela última vez.</summary>
    public DateTime RespondidaEm { get; set; }

    public bool Vencida(DateTime agoraUtc) => agoraUtc.Date > Ate.Date;
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
    /// Junta as conversas recém-respondidas às que já estavam vigiadas e joga fora as vencidas.
    /// <para>
    /// Função pura sobre o que entra, para poder ser testada sem disco nem relógio da máquina.
    /// Reencontrar uma conversa já vigiada RENOVA o prazo em vez de duplicá-la: continuar
    /// respondendo é continuar esperando retorno.
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
            if (string.IsNullOrWhiteSpace(v.Thrid) || v.Vencida(agoraUtc)) continue;
            porThread[v.Thrid] = v;
        }

        foreach (var r in respondidas ?? Array.Empty<ThreadRespondida>())
        {
            if (string.IsNullOrWhiteSpace(r.Thrid)) continue;

            porThread[r.Thrid] = new VigiaDeThread
            {
                Thrid = r.Thrid,
                Porque = $"você respondeu em {r.QuandoUtc.ToLocalTime():dd/MM}; aguarda retorno",
                RespondidaEm = r.QuandoUtc,
                Ate = r.QuandoUtc.AddDays(DiasDeVigia),
                Acorda9b = false
            };
        }

        return porThread.Values.OrderByDescending(v => v.RespondidaEm).ToList();
    }
}

/// <summary>Uma conversa em que o usuário escreveu, vinda de <c>[Gmail]/Sent</c>.</summary>
/// <param name="Thrid">X-GM-THRID.</param>
/// <param name="QuandoUtc">Quando ele escreveu.</param>
public readonly record struct ThreadRespondida(string Thrid, DateTime QuandoUtc);
