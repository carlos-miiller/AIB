using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIB.Services;

/// <summary>
/// Os atributos de um personagem, de 1 a 5. O 3 é o neutro: o comportamento que valia para todos
/// antes de haver atributo. O que cada nível vale está em <see cref="Temperamento.De"/>.
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

    /// <summary>O quanto uma boa conversa o deixa mais inclinado a voltar.</summary>
    public int Apego { get; set; } = Neutro;

    /// <summary>O quanto ele aguenta ser ignorado ou ouvir um "agora não" sem se retrair.</summary>
    public int Resiliencia { get; set; } = Neutro;

    /// <summary>O quanto o que ele aprendeu dura: alto, esquece devagar.</summary>
    public int Constancia { get; set; } = Neutro;

    /// <summary>O quanto ele prefere perguntar sobre o usuário a retomar um assunto.</summary>
    public int Curiosidade { get; set; } = Neutro;
}

/// <summary>
/// O arquivo de status: <c>~/.AIB/character/status.json</c>, um registro por personagem.
/// <para>
/// NASCE VAZIO — decisão do usuário: "não vamos trazer o arquivo já com a info dos personagens
/// preenchida... em qualquer momento podemos introduzir outro personagem e ele ser adicionado".
/// O personagem entra na primeira vez em que é o ativo, com tudo no neutro, e dali em diante o
/// arquivo é de quem o edita: o AIB só acrescenta quem falta, nunca mexe em quem já está.
/// </para>
/// <para>
/// Texto claro, como o <c>info.json</c>: é configuração do personagem, não conversa nem fato do
/// usuário, e precisa ser editável à mão.
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
        _arquivo = Path.Combine(raiz ?? DirectoryService.DataDir, "character", "status.json");

    public string Arquivo => _arquivo;

    /// <summary>Quando o arquivo foi gravado pela última vez: é como se nota a edição à mão.</summary>
    public DateTime AlteradoUtc => File.Exists(_arquivo) ? File.GetLastWriteTimeUtc(_arquivo) : DateTime.MinValue;

    /// <summary>
    /// Os atributos do personagem, lidos do arquivo agora.
    /// Quem não está lá é acrescentado no neutro. Sem nome, ou com o arquivo ilegível, o neutro —
    /// e o arquivo ilegível fica como está, para a edição do usuário não se perder.
    /// </summary>
    public Atributos De(string? personagem)
    {
        string nome = (personagem ?? "").Trim();
        if (nome.Length == 0) return new Atributos();

        lock (_gate)
        {
            try
            {
                var todos = Ler();
                string? chave = todos.Keys.FirstOrDefault(k => string.Equals(k, nome, StringComparison.OrdinalIgnoreCase));
                if (chave != null) return todos[chave] ?? new Atributos();

                todos[nome] = new Atributos();
                Gravar(todos);
                return todos[nome];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STATUS] {_arquivo} não foi lido, valem os atributos neutros: {ex.Message}");
                return new Atributos();
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
