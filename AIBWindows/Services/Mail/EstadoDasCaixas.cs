using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AIB.Services.Mail;

/// <summary>O que se sabe de uma caixa entre uma varredura e a seguinte.</summary>
public sealed class EstadoDaCaixa
{
    /// <summary>
    /// Selo de validade dos UIDs no servidor. Se ele muda, TODO UID guardado virou lixo — o
    /// servidor renumerou a caixa. É a diferença entre "li da mensagem 91043 para frente" e
    /// "reli a caixa inteira achando que estava em dia".
    /// </summary>
    public uint UidValidity { get; set; }

    /// <summary>Maior UID já visto. Zero quando nunca se leu nada.</summary>
    public uint LastUid { get; set; }

    public DateTime? LastReadUtc { get; set; }
}

/// <summary>
/// O <c>estado.json</c> de <c>~/.AIB/email/</c> — §Arquivos de ideias_futuras/07_Vigia_de_Email.
/// <para>
/// Um arquivo só, uma entrada por caixa, CHAVEADA PELO ENDEREÇO. Chavear por rótulo abria a
/// classe de bug em que renomear a caixa na tela faz o vigia perder o <c>lastUid</c> e retriar
/// semanas de mensagem; o endereço é a identidade e não muda sem a conta mudar junto.
/// </para>
/// <para>
/// NENHUM CORPO DE MENSAGEM EM DISCO. Aqui só entram números de controle: validade, último UID
/// e quando foi. É a regra 3 do documento do vigia aplicada no lugar mais fácil de violar.
/// </para>
/// </summary>
public sealed class EstadoDasCaixas
{
    private readonly string _caminho;

    private static readonly JsonSerializerOptions Formato = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <param name="raizDeDados">
    /// Sobrescreve <c>~/.AIB</c>. Existe para os ensaios: escrever no estado real durante um
    /// teste faria o vigia do usuário reler ou pular mensagens de verdade.
    /// </param>
    public EstadoDasCaixas(string? raizDeDados = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        _caminho = Path.Combine(baseDir, "email", "estado.json");
    }

    public string Caminho => _caminho;

    /// <summary>O que se sabe da caixa, ou <c>null</c> quando ela nunca foi lida.</summary>
    public EstadoDaCaixa? Ler(string endereco)
    {
        var todos = LerTudo();
        return todos.TryGetValue(Chave(endereco), out var estado) ? estado : null;
    }

    public void Gravar(string endereco, EstadoDaCaixa estado)
    {
        var todos = LerTudo();
        todos[Chave(endereco)] = estado;
        GravarTudo(todos);
    }

    /// <summary>Some com a caixa. Chamado junto da remoção da conta.</summary>
    public void Remover(string endereco)
    {
        var todos = LerTudo();
        if (todos.Remove(Chave(endereco))) GravarTudo(todos);
    }

    /// <summary>
    /// Decide de onde a próxima varredura parte.
    /// <para>
    /// Sem estado, ou com a validade trocada, volta pela DATA — §Decisões: triar backlog é
    /// trabalho jogado fora, ninguém lê 300 pendências de três meses. Com a validade batendo,
    /// parte do último UID e só vê o que chegou depois.
    /// </para>
    /// </summary>
    public uint UidDePartida(string endereco, uint uidValidityAtual)
    {
        var estado = Ler(endereco);
        if (estado == null) return 0;
        return estado.UidValidity == uidValidityAtual ? estado.LastUid : 0;
    }

    private static string Chave(string endereco) => (endereco ?? "").Trim().ToLowerInvariant();

    private Dictionary<string, EstadoDaCaixa> LerTudo()
    {
        try
        {
            if (!File.Exists(_caminho)) return new Dictionary<string, EstadoDaCaixa>();

            string json = File.ReadAllText(_caminho);
            var lido = JsonSerializer.Deserialize<Dictionary<string, EstadoDaCaixa>>(json, Formato);
            return lido ?? new Dictionary<string, EstadoDaCaixa>();
        }
        catch (Exception ex)
        {
            // Arquivo corrompido não pode impedir a varredura: sem estado, ela reparte pela
            // data, que é o comportamento de primeira execução — pior, mas correto.
            Console.WriteLine($"[EMAIL] estado.json ilegível ({ex.Message}); recomeçando pela data.");
            return new Dictionary<string, EstadoDaCaixa>();
        }
    }

    private void GravarTudo(Dictionary<string, EstadoDaCaixa> todos)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_caminho)!);
            File.WriteAllText(_caminho, JsonSerializer.Serialize(todos, Formato));
        }
        catch (Exception ex)
        {
            // Perder o estado custa uma releitura na próxima vez. Derrubar a varredura por
            // causa disso custaria a varredura inteira.
            Console.WriteLine($"[EMAIL] não consegui gravar o estado.json: {ex.Message}");
        }
    }
}
