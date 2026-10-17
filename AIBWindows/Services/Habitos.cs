using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIB.Services;

/// <summary>Um jeito de agir que o personagem pegou na convivência com o usuário.</summary>
public sealed class Habito
{
    /// <summary>O jeito, começando por verbo e sem sujeito: "faz trocadilhos com esgrima".</summary>
    public string Texto { get; set; } = "";

    /// <summary>Verdadeiro: o usuário NÃO gostou, e o personagem evita.</summary>
    public bool Evita { get; set; }

    /// <summary>A força quando foi visto pela última vez; esquece a partir daí (<see cref="Habitos.ForcaEm"/>).</summary>
    public double Forca { get; set; }

    /// <summary>Quantas vezes o resumidor viu a reação a esse jeito.</summary>
    public int Vezes { get; set; }

    public DateTime VistoUtc { get; set; }
}

/// <summary>Uma reação do usuário a um jeito do personagem, como o resumidor a leu num trecho.</summary>
/// <param name="Gostou">Se a reação foi boa.</param>
/// <param name="Numero">O número do jeito na lista que o resumidor recebeu (1 em diante); nulo é jeito novo.</param>
/// <param name="Jeito">O jeito, quando é novo.</param>
/// <param name="Fala">A fala do usuário que mostra a reação, já conferida contra o trecho.</param>
public sealed record Reacao(bool Gostou, int? Numero, string Jeito, string Fala);

/// <summary>
/// Os hábitos que cada personagem pega na convivência — pedido do usuário: "se ela faz algo e eu
/// reajo positivamente a isso, de forma orgânica".
/// <para>
/// O RESUMIDOR VÊ, O CÓDIGO DECIDE. Ao fechar um capítulo, o resumidor aponta o jeito do agente
/// que o usuário recebeu bem ou mal, com a fala dele (<see cref="Memory.Compactor"/>); aqui cada
/// reação vira força. Um jeito novo nasce fraco, e só entra no prompt visto de novo
/// (<see cref="VezesMinimas"/>, <see cref="Limiar"/>): uma reação mal lida não vira regra.
/// </para>
/// <para>
/// ESQUECE DEVAGAR. A força cai pela constância do personagem a cada SEMANA sem ser vista — a
/// mesma escala que o multiplicador geral da iniciativa usa por dia, porque hábito dura mais que
/// humor. A curiosidade dá a força com que um jeito novo nasce. Reação contrária derruba
/// <see cref="Contraria"/> de uma vez: desgostar pesa mais que gostar.
/// </para>
/// <para>
/// Um arquivo por personagem, cifrado (<see cref="ArquivoCifrado"/>), em
/// <c>~/.AIB/memory/shadow/habitos/&lt;Nome&gt;.dat</c>. O texto é do resumidor e cita a conversa;
/// por isso cifra, como o vínculo.
/// </para>
/// </summary>
public sealed class Habitos
{
    /// <summary>Quantos ele guarda. Passando disso, sai o mais fraco.</summary>
    public const int Guardados = 12;

    /// <summary>Quantos vão ao prompt, os mais fortes.</summary>
    public const int NoPrompt = 5;

    public const double Teto = 5;

    /// <summary>A força a partir da qual o jeito vai ao prompt.</summary>
    public const double Limiar = 1.5;

    /// <summary>Quantas vezes o jeito precisa ter sido visto para ir ao prompt.</summary>
    public const int VezesMinimas = 2;

    public const double Reforco = 1;
    public const double Contraria = 2;

    /// <summary>Abaixo disso o jeito foi esquecido e sai do arquivo.</summary>
    public const double Some = 0.25;

    public const int TetoDoTexto = 120;

    /// <summary>A força de um jeito novo, pela curiosidade (1 a 5).</summary>
    private static readonly double[] NascePorNivel = { 0.5, 0.75, 1.0, 1.25, 1.5 };

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    // Estático: as duas conversas (janela e orbe) fecham capítulos e gravam no mesmo arquivo.
    private static readonly object Gate = new();

    private readonly string _pasta;

    /// <param name="raiz">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public Habitos(string? raiz = null) =>
        _pasta = Path.Combine(raiz ?? RaizPadrao ?? DirectoryService.DataDir, "memory", ConversaDoOrbe.Sessao, "habitos");

    /// <summary>A raiz quando ninguém passa uma. Existe para a suíte desviar de <c>~/.AIB</c>.</summary>
    public static string? RaizPadrao { get; set; }

    /// <summary>A força de agora: a gravada, menos o esquecimento das semanas sem ser visto.</summary>
    /// <param name="esquecimento">Quanto cai por semana (<see cref="Temperamento.Esquecimento"/>).</param>
    public static double ForcaEm(Habito h, DateTime agoraUtc, double esquecimento)
    {
        double semanas = Math.Max(0, (agoraUtc - h.VistoUtc).TotalDays / 7);
        return h.Forca * Math.Pow(1 - Math.Clamp(esquecimento, 0, 0.9), semanas);
    }

    public static double Nasce(int curiosidade) =>
        NascePorNivel[Math.Clamp(curiosidade <= 0 ? Atributos.Neutro : curiosidade, 1, NascePorNivel.Length) - 1];

    /// <summary>Todos os guardados do personagem, na ordem do arquivo. Vazio sem nome ou sem arquivo.</summary>
    public IReadOnlyList<Habito> De(string? personagem)
    {
        lock (Gate) return Ler(personagem);
    }

    /// <summary>Os que vão ao prompt: vistos o bastante e fortes o bastante, os mais fortes primeiro.</summary>
    public IReadOnlyList<Habito> Ativos(string? personagem, DateTime agoraUtc, double esquecimento) =>
        De(personagem)
            .Where(h => h.Vezes >= VezesMinimas && ForcaEm(h, agoraUtc, esquecimento) >= Limiar)
            .OrderByDescending(h => ForcaEm(h, agoraUtc, esquecimento))
            .Take(NoPrompt)
            .ToList();

    /// <summary>
    /// Aplica as reações de um trecho. <paramref name="conhecidos"/> é a lista que o resumidor
    /// recebeu: o número de uma reação aponta para ela, e o jeito é achado no arquivo pelo
    /// TEXTO — o arquivo pode ter mudado enquanto o resumo rodava.
    /// </summary>
    /// <returns>O que mudou, em palavras, para o console.</returns>
    public IReadOnlyList<string> Aprender(
        string? personagem, IReadOnlyList<Reacao> reacoes, IReadOnlyList<Habito> conhecidos,
        Atributos? atributos, DateTime agoraUtc)
    {
        var mudou = new List<string>();
        if (Arquivo(personagem) == null || reacoes.Count == 0) return mudou;

        double esquecimento = Temperamento.De(atributos).Esquecimento;
        double nasce = Nasce(atributos?.Curiosidade ?? Atributos.Neutro);

        lock (Gate)
        {
            var lista = Ler(personagem).ToList();

            foreach (var r in reacoes)
            {
                string? texto = r.Numero is int n && n >= 1 && n <= conhecidos.Count
                    ? conhecidos[n - 1].Texto
                    : Aparar(r.Jeito);
                if (string.IsNullOrEmpty(texto)) continue;

                var h = lista.FirstOrDefault(x => string.Equals(x.Texto, texto, StringComparison.OrdinalIgnoreCase));
                if (h == null)
                {
                    // Número que não aponta para nada é erro do resumidor, não jeito novo.
                    if (r.Numero != null) continue;

                    lista.Add(new Habito { Texto = texto, Evita = !r.Gostou, Forca = nasce, Vezes = 1, VistoUtc = agoraUtc });
                    mudou.Add($"novo: {(r.Gostou ? "+" : "-")} {texto}");
                    continue;
                }

                double atual = ForcaEm(h, agoraUtc, esquecimento);
                bool mesmoLado = r.Gostou != h.Evita;
                h.Forca = mesmoLado ? Math.Min(Teto, atual + Reforco) : atual - Contraria;
                if (mesmoLado) h.Vezes++;
                h.VistoUtc = agoraUtc;
                mudou.Add($"{(mesmoLado ? "reforçado" : "contrariado")}: {texto} ({h.Forca:0.00})");
            }

            var ficam = lista
                .Where(h => ForcaEm(h, agoraUtc, esquecimento) >= Some)
                .OrderByDescending(h => ForcaEm(h, agoraUtc, esquecimento))
                .Take(Guardados)
                .ToHashSet();
            lista.RemoveAll(h => !ficam.Contains(h));

            Gravar(personagem, lista);
        }
        return mudou;
    }

    /// <summary>Tira um jeito — o usuário, nas configurações.</summary>
    public void Esquecer(string? personagem, string texto)
    {
        lock (Gate)
        {
            var lista = Ler(personagem).ToList();
            if (lista.RemoveAll(h => h.Texto == texto) > 0) Gravar(personagem, lista);
        }
    }

    /// <summary>
    /// A linha do contexto do prompt, no formato da linha do afeto. Vazia sem hábito ativo: o
    /// prompt de quem ainda não pegou nenhum fica byte a byte o que era.
    /// </summary>
    public static string Linha(IReadOnlyList<Habito> ativos)
    {
        if (ativos.Count == 0) return "";

        var faz = ativos.Where(h => !h.Evita).Select(h => h.Texto).ToList();
        var evita = ativos.Where(h => h.Evita).Select(h => h.Texto).ToList();

        var texto = new StringBuilder("\n- Jeitos da convivência: com o tempo você notou que o usuário");
        if (faz.Count > 0) texto.Append(" gosta quando você ").Append(string.Join("; ", faz));
        if (faz.Count > 0 && evita.Count > 0) texto.Append(", e");
        if (evita.Count > 0) texto.Append(" não gosta quando você ").Append(string.Join("; ", evita));
        texto.Append(". Use isso com naturalidade, de vez em quando, sem deixar de ser quem você é.");
        return texto.ToString();
    }

    private static string Aparar(string? jeito)
    {
        string t = System.Text.RegularExpressions.Regex.Replace((jeito ?? "").Trim(), @"\s+", " ").Trim('.', ';', ' ');
        return t.Length <= TetoDoTexto ? t : t[..TetoDoTexto].TrimEnd();
    }

    private string? Arquivo(string? personagem)
    {
        string nome = Path.GetFileName((personagem ?? "").Trim());
        if (nome.Length == 0 || nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return Path.Combine(_pasta, nome + ".dat");
    }

    private List<Habito> Ler(string? personagem)
    {
        string? arquivo = Arquivo(personagem);
        if (arquivo == null) return new();

        try
        {
            string? json = ArquivoCifrado.Ler(arquivo);
            return json == null ? new() : JsonSerializer.Deserialize<List<Habito>>(json, Json) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HABITOS] Falha ao ler {arquivo}: {ex.Message}");
            return new();
        }
    }

    private void Gravar(string? personagem, List<Habito> lista)
    {
        string? arquivo = Arquivo(personagem);
        if (arquivo == null) return;

        try
        {
            Directory.CreateDirectory(_pasta);
            ArquivoCifrado.Gravar(arquivo, JsonSerializer.Serialize(lista, Json));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HABITOS] Falha ao gravar {arquivo}: {ex.Message}");
        }
    }
}
