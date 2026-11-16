using System.Text.Json.Serialization;

namespace AIB.Services;

public class AgentStats
{
    public int Assertiveness { get; set; }
    public int Usefulness { get; set; }
    public int Humanity { get; set; }
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

    /// <summary>
    /// Os atributos de fábrica do personagem (1 a 5). Os que valem agora ficam no arquivo de
    /// status (<see cref="StatusDosPersonagens"/>), que parte destes.
    /// </summary>
    public Atributos? Atributos { get; set; }

    [JsonIgnore]
    public bool IsSelected { get; set; }
}
