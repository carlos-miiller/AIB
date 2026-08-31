using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIB.Services;

/// <summary>
/// Uma habilidade instalada: um script em disco com um cabeçalho que o descreve.
/// </summary>
public class LocalSkill
{
    /// <summary>Nome pelo qual o modelo a chama. Vem do cabeçalho, não da pasta.</summary>
    public string Name { get; set; } = "";

    /// <summary>Uma linha dizendo para que serve. É o que entra no prompt.</summary>
    public string Description { get; set; } = "";

    /// <summary>Como rodar: <c>powershell</c>, <c>python</c> ou <c>markdown</c>.</summary>
    public string Interpreter { get; set; } = "";

    /// <summary>Caminho absoluto do script. Vazio quando o arquivo declarado não existe.</summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Pasta da skill. Vira diretório de trabalho na execução.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Corpo do SKILL.md depois do cabeçalho — instruções de uso, para o modelo ler.</summary>
    public string Instructions { get; set; } = "";
}

/// <summary>
/// Lê as habilidades instaladas em <c>~/.AIB/skills</c>.
/// <para>
/// Cada skill é uma pasta com um <c>SKILL.md</c>: cabeçalho entre linhas de <c>---</c>
/// declarando nome, descrição, interpretador e arquivo de script, seguido do texto livre que
/// explica como usá-la.
/// </para>
/// <para>
/// Só o nome e a descrição entram no prompt de sistema — o corpo é entregue ao modelo apenas
/// quando ele pede a skill. É o "lazy loading" que o <see cref="ToolRegistry"/> documenta:
/// vinte skills instaladas custam vinte linhas no prompt, não vinte schemas de ferramenta.
/// </para>
/// </summary>
public static class SkillService
{
    /// <summary>
    /// Raiz alternativa para ensaios. Mesmo motivo do override do histórico e da auditoria: a
    /// suíte não pode ler nem escrever nas skills reais do usuário.
    /// </summary>
    public static string? SkillsDirectoryOverride { get; set; }

    public static string Raiz => SkillsDirectoryOverride ?? DirectoryService.SkillsDir;

    public static int GetSkillCount() => ListLocalSkills().Count;

    /// <summary>
    /// Todas as skills legíveis, em ordem alfabética. Uma pasta quebrada é ignorada em
    /// silêncio: uma skill malformada não pode impedir as outras de existirem, e muito menos
    /// derrubar a montagem do prompt de sistema.
    /// </summary>
    public static List<LocalSkill> ListLocalSkills()
    {
        var skills = new List<LocalSkill>();

        try
        {
            if (!Directory.Exists(Raiz)) return skills;

            foreach (var pasta in Directory.GetDirectories(Raiz))
            {
                var skill = Ler(pasta);
                if (skill != null) skills.Add(skill);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SKILLS] Falha ao listar: {ex.Message}");
        }

        return skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Busca por nome, sem diferenciar maiúsculas. Null quando não existe.</summary>
    public static LocalSkill? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return ListLocalSkills()
            .FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lê uma pasta de skill. Devolve null quando não há SKILL.md, quando o cabeçalho não tem
    /// nome, ou quando o arquivo não pode ser lido.
    /// </summary>
    private static LocalSkill? Ler(string pasta)
    {
        try
        {
            string caminho = Path.Combine(pasta, "SKILL.md");
            if (!File.Exists(caminho)) return null;

            string texto = File.ReadAllText(caminho);
            var (cabecalho, corpo) = SepararCabecalho(texto);

            string nome = Valor(cabecalho, "name");

            // Sem nome não há como o modelo chamá-la. O nome da pasta NÃO serve de substituto:
            // as instruções dentro do arquivo se referem ao nome declarado, e adivinhar aqui
            // produziria uma skill que o modelo chama por um nome e que documenta outro.
            if (string.IsNullOrWhiteSpace(nome)) return null;

            string script = Valor(cabecalho, "script_file");
            string caminhoScript = string.IsNullOrWhiteSpace(script)
                ? ""
                : Path.Combine(pasta, script);

            return new LocalSkill
            {
                Name = nome,
                Description = Valor(cabecalho, "description"),
                Interpreter = Valor(cabecalho, "interpreter"),
                ScriptPath = File.Exists(caminhoScript) ? caminhoScript : "",
                Folder = pasta,
                Instructions = corpo.Trim()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SKILLS] Ignorando '{pasta}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Separa o cabeçalho delimitado por <c>---</c> do corpo.
    /// <para>
    /// Não é um parser de YAML e não pretende ser: aceita <c>chave: valor</c> por linha e nada
    /// mais. Um YAML de verdade traria listas, aninhamento e âncoras — superfície que ninguém
    /// pediu, num arquivo que o usuário escreve à mão.
    /// </para>
    /// </summary>
    private static (List<string> Cabecalho, string Corpo) SepararCabecalho(string texto)
    {
        var linhas = texto.Replace("\r\n", "\n").Split('\n');
        var cabecalho = new List<string>();

        int i = 0;
        while (i < linhas.Length && linhas[i].Trim().Length == 0) i++;

        if (i >= linhas.Length || linhas[i].Trim() != "---")
            return (cabecalho, texto);

        i++;
        while (i < linhas.Length && linhas[i].Trim() != "---")
        {
            cabecalho.Add(linhas[i]);
            i++;
        }

        i++; // pula o --- de fechamento
        string corpo = i < linhas.Length ? string.Join("\n", linhas.Skip(i)) : "";

        return (cabecalho, corpo);
    }

    private static string Valor(List<string> cabecalho, string chave)
    {
        foreach (var linha in cabecalho)
        {
            int sep = linha.IndexOf(':');
            if (sep <= 0) continue;

            if (!linha.Substring(0, sep).Trim().Equals(chave, StringComparison.OrdinalIgnoreCase))
                continue;

            return linha.Substring(sep + 1).Trim().Trim('"', '\'');
        }

        return "";
    }
}
