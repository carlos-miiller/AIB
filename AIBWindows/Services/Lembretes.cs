using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIB.Services;

/// <summary>Um lembrete pedido pelo usuário: o texto já escrito pela persona, e a hora.</summary>
/// <param name="Id">Curto, para o usuário cancelar pelo nome ("cancela o a3f").</param>
/// <param name="QuandoUtc">A hora de entregar.</param>
/// <param name="Texto">A fala da persona, escrita no pedido e entregue como está.</param>
/// <param name="Assunto">Do que se trata, em poucas palavras, para listar.</param>
public sealed record Lembrete(string Id, DateTime QuandoUtc, string Texto, string Assunto);

/// <summary>
/// Os lembretes únicos pendentes, em <c>~/.AIB/lembretes.json</c>.
/// <para>
/// O texto é escrito pela persona NA HORA DO PEDIDO e entregue como está: disparar não chama o
/// modelo, e por isso não custa nada. Entregue, o lembrete sai do arquivo — ele é único.
/// </para>
/// <para>
/// O PC desligado na hora não perde o lembrete: no arranque, os vencidos são entregues com a
/// hora em que deviam ter chegado (<see cref="Atrasado"/>).
/// </para>
/// </summary>
public sealed class Lembretes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    /// <summary>Mais que isso é lista de tarefas, não lembrete.</summary>
    public const int Teto = 50;

    /// <summary>Atraso a partir do qual a entrega diz que devia ter chegado antes.</summary>
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Estático: a ferramenta e a agenda do App têm cada uma a sua instância, sobre o MESMO
    /// arquivo. Com trava por instância, criar e retirar ao mesmo tempo perderia um dos dois.
    /// </summary>
    private static readonly object _gate = new();
    private readonly string _arquivo;

    /// <param name="raiz">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public Lembretes(string? raiz = null) =>
        _arquivo = Path.Combine(raiz ?? DirectoryService.DataDir, "lembretes.json");

    public string Arquivo => _arquivo;

    /// <summary>Os pendentes, do mais próximo ao mais distante.</summary>
    public IReadOnlyList<Lembrete> Listar()
    {
        lock (_gate) return Ler().OrderBy(l => l.QuandoUtc).ToList();
    }

    /// <summary>Agenda. Devolve o lembrete criado, ou null no teto.</summary>
    public Lembrete? Criar(DateTime quandoUtc, string texto, string assunto)
    {
        lock (_gate)
        {
            var todos = Ler();
            if (todos.Count >= Teto) return null;

            string id;
            do id = Guid.NewGuid().ToString("N")[..4];
            while (todos.Any(l => l.Id == id));

            var novo = new Lembrete(id, quandoUtc, texto.Trim(), assunto.Trim());
            todos.Add(novo);
            Gravar(todos);
            return novo;
        }
    }

    /// <summary>Tira o lembrete. Devolve se existia.</summary>
    public bool Cancelar(string id)
    {
        lock (_gate)
        {
            var todos = Ler();
            int removidos = todos.RemoveAll(l => string.Equals(l.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removidos > 0) Gravar(todos);
            return removidos > 0;
        }
    }

    /// <summary>
    /// Tira e devolve os que venceram até <paramref name="agoraUtc"/>. Quem chama entrega; se
    /// a entrega falhar, o lembrete já saiu — antes perder um aviso que repeti-lo a cada batida.
    /// </summary>
    public IReadOnlyList<Lembrete> Retirar(DateTime agoraUtc)
    {
        lock (_gate)
        {
            var todos = Ler();
            var vencidos = todos.Where(l => l.QuandoUtc <= agoraUtc).OrderBy(l => l.QuandoUtc).ToList();
            if (vencidos.Count == 0) return vencidos;

            todos.RemoveAll(l => l.QuandoUtc <= agoraUtc);
            Gravar(todos);
            return vencidos;
        }
    }

    /// <summary>
    /// O texto da entrega. Atrasado (PC desligado, app fechado), diz para quando era: um
    /// "sua reunião começa agora" lido duas horas depois mente.
    /// </summary>
    public static string Atrasado(Lembrete l, DateTime agoraUtc)
    {
        if (agoraUtc - l.QuandoUtc <= Tolerancia) return l.Texto;

        var local = l.QuandoUtc.ToLocalTime();
        string quando = local.Date == agoraUtc.ToLocalTime().Date
            ? local.ToString("HH:mm")
            : local.ToString("dd/MM 'às' HH:mm");
        return $"(Era para {quando}.) {l.Texto}";
    }

    private List<Lembrete> Ler()
    {
        try
        {
            if (!File.Exists(_arquivo)) return new List<Lembrete>();
            return JsonSerializer.Deserialize<List<Lembrete>>(ArquivoCifrado.Ler(_arquivo) ?? "[]", Json)
                   ?? new List<Lembrete>();
        }
        catch (Exception ex)
        {
            // Arquivo corrompido não derruba a conversa; os lembretes dele se perdem, e o log diz.
            Console.WriteLine($"[LEMBRETES] Falha ao ler {_arquivo}: {ex.Message}");
            return new List<Lembrete>();
        }
    }

    private void Gravar(List<Lembrete> todos)
    {
        // Cifrado: o texto do lembrete é fala sobre a vida do usuário.
        ArquivoCifrado.GravarTexto(_arquivo, JsonSerializer.Serialize(todos, Json));
    }
}
