using System.Text.Json.Serialization;

namespace AIB.Services;

public class AgentStats
{
    public int Assertiveness { get; set; }
    public int Usefulness { get; set; }
    public int Humanity { get; set; }
}

/// <summary>
/// O temperamento do personagem na iniciativa (<see cref="Temperamento"/>). Opcional no
/// info.json: sem ele, ou com zero, vale o padrão.
/// </summary>
public class AgentTemperament
{
    /// <summary>Multiplica a chance de ele puxar assunto. 1 é o padrão (0,1 a 3).</summary>
    public double Initiative { get; set; } = 1;

    /// <summary>O teto do quanto uma boa conversa o deixa mais inclinado a falar. Padrão 1,70 (1,10 a 2,50).</summary>
    public double Attachment { get; set; } = Iniciativa.TetoDaConversa;
}

public class AgentProfile
{
    [JsonIgnore]
    public string DirectoryName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    
    [JsonPropertyName("Sample-speech")]
    public string SampleSpeech { get; set; } = string.Empty;
    
    public AgentStats Stats { get; set; } = new();

    public AgentTemperament? Temperament { get; set; }

    [JsonIgnore]
    public bool IsSelected { get; set; }
}
