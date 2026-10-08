using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIB.Services;

/// <summary>
/// Os atributos de um personagem. Quatro vão de 1 a 5, com o 3 de neutro: o comportamento que
/// valia para todos antes de haver atributo. O afeto vai de -5 a 5, com o 0 de neutro. O que
/// cada nível vale está em <see cref="Temperamento.De"/>.
/// <para>
/// Não são as estrelas da tela de escolha (<see cref="AgentStats"/>): aquelas descrevem o
/// personagem para quem escolhe, estas mexem na conta da iniciativa.
/// </para>
/// </summary>
public sealed class Atributos
{
    public const int Neutro = 3;

    /// <summary>O quanto ele puxa assunto por conta própria.</summary>
    public int Iniciativa { get; set; } = Neutro;

    public const double AfetoMinimo = -5;
    public const double AfetoMaximo = 5;

    /// <summary>O quanto o afeto de agora pode se afastar do de fábrica: a Kai nunca vira a Sora.</summary>
    public const double FolgaDoAfeto = 2;

    /// <summary>
    /// De -5 a 5 — decisão do usuário: "-5 o personagem é totalmente direto e evita interações
    /// prolongadas, e 5 é extremamente expressivo, apegado e adora interagir". É o único que
    /// anda sozinho (<see cref="StatusDosPersonagens.Mover"/>), e por isso tem fração.
    /// </summary>
    public double Afeto { get; set; }

    /// <summary>O quanto ele aguenta ser ignorado ou ouvir um "agora não" sem se retrair.</summary>
    public int Resiliencia { get; set; } = Neutro;

    /// <summary>O quanto o que ele aprendeu dura: alto, esquece devagar.</summary>
    public int Constancia { get; set; } = Neutro;

    /// <summary>O quanto ele prefere perguntar sobre o usuário a retomar um assunto.</summary>
    public int Curiosidade { get; set; } = Neutro;
}

/// <summary>
/// O arquivo de status: <c>~/.AIB/memory/shadow/status.json</c>, um registro por personagem,
/// com os atributos como estão AGORA. Os de fábrica ficam no <c>info.json</c> de cada um
/// (<see cref="AgentProfile.Atributos"/>) — decisão do usuário: "vamos salvar no info os stats
/// padrões; em memory/shadow ficarão os modificados".
/// <para>
/// NASCE VAZIO: "em qualquer momento podemos introduzir outro personagem e ele ser adicionado
/// no arquivo de status". O personagem entra na primeira vez em que é o ativo, com os padrões
/// do <c>info.json</c> dele (ou tudo no neutro, se não tiver), e dali em diante o que vale é o
/// que está aqui: o AIB só acrescenta quem falta, nunca mexe em quem já está.
/// </para>
/// <para>
/// Texto claro: são cinco números do personagem, não conversa nem fato do usuário, e precisam
/// ser editáveis à mão.
/// </para>
/// </summary>
public sealed class StatusDosPersonagens
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _arquivo;
    private readonly object _gate = new();

    /// <param name="raiz">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public StatusDosPersonagens(string? raiz = null) =>
        _arquivo = Path.Combine(raiz ?? DirectoryService.DataDir, "memory", ConversaDoOrbe.Sessao, "status.json");

    public string Arquivo => _arquivo;

    /// <summary>Quando o arquivo foi gravado pela última vez: é como se nota a edição à mão.</summary>
    public DateTime AlteradoUtc => File.Exists(_arquivo) ? File.GetLastWriteTimeUtc(_arquivo) : DateTime.MinValue;

    /// <summary>
    /// Os atributos do personagem, lidos do arquivo agora.
    /// Quem não está lá é acrescentado com <paramref name="padrao"/> (os do info.json dele; sem
    /// eles, o neutro). Sem nome, ou com o arquivo ilegível, vale o padrão — e o arquivo
    /// ilegível fica como está, para a edição do usuário não se perder.
    /// </summary>
    public Atributos De(string? personagem, Atributos? padrao = null)
    {
        // Cópia: o registro do arquivo não pode ser o mesmo objeto do perfil lido do info.json.
        var inicial = new Atributos
        {
            Iniciativa = padrao?.Iniciativa ?? Atributos.Neutro,
            Afeto = padrao?.Afeto ?? 0,
            Resiliencia = padrao?.Resiliencia ?? Atributos.Neutro,
            Constancia = padrao?.Constancia ?? Atributos.Neutro,
            Curiosidade = padrao?.Curiosidade ?? Atributos.Neutro
        };

        string nome = (personagem ?? "").Trim();
        if (nome.Length == 0) return inicial;

        lock (_gate)
        {
            try
            {
                var todos = Ler();
                string? chave = todos.Keys.FirstOrDefault(k => string.Equals(k, nome, StringComparison.OrdinalIgnoreCase));
                if (chave != null) return todos[chave] ?? inicial;

                todos[nome] = inicial;
                Gravar(todos);
                return inicial;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STATUS] {_arquivo} não foi lido, valem os atributos padrão: {ex.Message}");
                return inicial;
            }
        }
    }

    /// <summary>
    /// Anda o afeto de agora do personagem: sobe com conversa boa, desce quando ele é ignorado
    /// ou recusado (<see cref="Iniciativa.AfetoMoveu"/>). Fica entre -5 e 5 e a no máximo
    /// <see cref="Atributos.FolgaDoAfeto"/> do afeto de fábrica. Quem não está no arquivo, ou
    /// arquivo ilegível: nada acontece.
    /// </summary>
    /// <param name="fabrica">O afeto do info.json do personagem.</param>
    public void Mover(string? personagem, double passo, double fabrica)
    {
        string nome = (personagem ?? "").Trim();
        if (nome.Length == 0 || passo == 0) return;

        lock (_gate)
        {
            try
            {
                var todos = Ler();
                string? chave = todos.Keys.FirstOrDefault(k => string.Equals(k, nome, StringComparison.OrdinalIgnoreCase));
                if (chave == null || todos[chave] is not Atributos a) return;

                double centro = Math.Clamp(fabrica, Atributos.AfetoMinimo, Atributos.AfetoMaximo);
                double piso = Math.Max(Atributos.AfetoMinimo, centro - Atributos.FolgaDoAfeto);
                double teto = Math.Min(Atributos.AfetoMaximo, centro + Atributos.FolgaDoAfeto);

                double novo = Math.Round(Math.Clamp(a.Afeto + passo, piso, teto), 2);
                if (novo == a.Afeto) return;

                a.Afeto = novo;
                Gravar(todos);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STATUS] O afeto de {nome} não foi gravado: {ex.Message}");
            }
        }
    }

    private Dictionary<string, Atributos> Ler()
    {
        if (!File.Exists(_arquivo)) return new();

        string texto = File.ReadAllText(_arquivo).Trim().TrimStart((char)0xFEFF);
        if (texto.Length == 0) return new();

        return JsonSerializer.Deserialize<Dictionary<string, Atributos>>(texto, Json) ?? new();
    }

    private void Gravar(Dictionary<string, Atributos> todos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
        string temporario = _arquivo + ".tmp";
        File.WriteAllText(temporario, JsonSerializer.Serialize(todos, Json));
        File.Move(temporario, _arquivo, overwrite: true);
    }
}
