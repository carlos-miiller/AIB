using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AIB.Services;

/// <summary>Como um arquivo entrou no contexto — §6.2 da spec de chat, a "pill de origem".</summary>
public enum ContextOrigin
{
    /// <summary>O usuário anexou pelo botão ou arrastando.</summary>
    AttachedByUser = 0,

    /// <summary>A IA leu.</summary>
    ReadByAi = 1,

    /// <summary>A IA criou ou sobrescreveu.</summary>
    CreatedByAi = 2,

    /// <summary>A IA copiou.</summary>
    CopiedByAi = 3
}

/// <summary>
/// Um arquivo encostado pela conversa.
/// <para>
/// <see cref="FilePath"/> é o caminho absoluto e literal — a mesma regra dos artefatos da
/// memória. Um caminho aproximado aqui vira Explorer abrindo a pasta errada.
/// </para>
/// </summary>
public sealed class ContextFile
{
    public required string FilePath { get; init; }

    public ContextOrigin Origin { get; set; } = ContextOrigin.AttachedByUser;

    /// <summary>Quando foi encostado pela última vez.</summary>
    public DateTime At { get; set; } = DateTime.Now;

    public string Name => Path.GetFileName(FilePath);

    public string Folder => Path.GetDirectoryName(FilePath) ?? "";

    public string Extension => Path.GetExtension(FilePath).TrimStart('.').ToLowerInvariant();

    /// <summary>Tamanho em disco, ou 0 quando o arquivo não existe mais.</summary>
    public long SizeBytes
    {
        get
        {
            try
            {
                var info = new FileInfo(FilePath);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                // Caminho inválido, unidade removida, permissão negada: tamanho desconhecido
                // não pode derrubar a lista.
                return 0;
            }
        }
    }

    /// <summary>Rótulo da pill de origem (§6.2 d).</summary>
    public string OriginLabel => Origin switch
    {
        ContextOrigin.CreatedByAi => "criado",
        ContextOrigin.CopiedByAi => "copiado",
        ContextOrigin.ReadByAi => "lido",
        _ => "anexado"
    };

    /// <summary>Se a ação foi da IA — a pill fica lilás; do usuário, cinza.</summary>
    public bool ByAi => Origin != ContextOrigin.AttachedByUser;
}

/// <summary>
/// Os arquivos que a conversa encostou — §6.2.
/// <para>
/// Antes disto o serviço era casca: <c>ActiveFiles</c> devolvia uma lista NOVA e vazia a cada
/// chamada, e <c>AddFile</c> não fazia nada. A aba do painel nunca mostrou um arquivo sequer, o
/// arrastar-e-soltar não guardava, e o <c>AddRecentFile</c> que o laço do agente chama a cada
/// leitura descartava em silêncio. Parecia funcionalidade e não era.
/// </para>
/// <para>
/// Estático porque o painel, a janela de chat e o laço do agente precisam da MESMA lista, e não
/// há um contêiner de injeção que os alcance. As coleções são observáveis para a interface
/// acompanhar sem consultar.
/// </para>
/// </summary>
public static class ContextService
{
    private static readonly object Trava = new();

    /// <summary>Teto da lista de recentes. Passado dele, o mais antigo sai.</summary>
    public const int MaxRecentes = 30;

    private static readonly ObservableCollection<ContextFile> Ativos = new();
    private static readonly ObservableCollection<ContextFile> Recentes = new();

    /// <summary>Arquivos no contexto desta conversa.</summary>
    public static ObservableCollection<ContextFile> ActiveFiles => Ativos;

    /// <summary>Últimos arquivos que a IA encostou, mesmo os já removidos do contexto.</summary>
    public static ObservableCollection<ContextFile> RecentFiles => Recentes;

    /// <summary>
    /// Acrescenta ou atualiza um arquivo no contexto.
    /// <para>
    /// Repetido não duplica: o mesmo caminho encostado de novo sobe para o topo e atualiza a
    /// origem. Uma lista com o mesmo arquivo cinco vezes não informa nada além de "foi lido
    /// cinco vezes", e isso é assunto do histórico de ações, não da lista de arquivos.
    /// </para>
    /// </summary>
    public static void AddFile(string? path, ContextOrigin origin = ContextOrigin.AttachedByUser)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string completo = Normalizar(path);
        if (completo.Length == 0) return;

        lock (Trava)
        {
            var existente = Ativos.FirstOrDefault(
                f => string.Equals(f.FilePath, completo, StringComparison.OrdinalIgnoreCase));

            if (existente != null)
            {
                existente.At = DateTime.Now;

                // Criado pela IA vence "lido": quem escreveu o arquivo fez mais do que abri-lo.
                if (origin > existente.Origin) existente.Origin = origin;

                Ativos.Remove(existente);
                Ativos.Insert(0, existente);
                return;
            }

            Ativos.Insert(0, new ContextFile { FilePath = completo, Origin = origin });
        }
    }

    /// <summary>Registra um arquivo tocado pela IA, sem trazê-lo para o contexto ativo.</summary>
    public static void AddRecentFile(string? path, ContextOrigin origin = ContextOrigin.ReadByAi)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string completo = Normalizar(path);
        if (completo.Length == 0) return;

        lock (Trava)
        {
            var existente = Recentes.FirstOrDefault(
                f => string.Equals(f.FilePath, completo, StringComparison.OrdinalIgnoreCase));

            if (existente != null) Recentes.Remove(existente);

            Recentes.Insert(0, new ContextFile
            {
                FilePath = completo,
                Origin = origin
            });

            while (Recentes.Count > MaxRecentes) Recentes.RemoveAt(Recentes.Count - 1);
        }
    }

    /// <summary>
    /// Tira o arquivo do CONTEXTO. Nunca apaga nada do disco — §6.2(e).
    /// </summary>
    public static void RemoveFile(ContextFile? file)
    {
        if (file == null) return;
        lock (Trava) Ativos.Remove(file);
    }

    /// <summary>Esvazia o contexto ativo. Os recentes ficam: eles são histórico, não estado.</summary>
    public static void Clear()
    {
        lock (Trava) Ativos.Clear();
    }

    /// <summary>Soma dos tamanhos dos arquivos no contexto, para o rodapé da aba.</summary>
    public static long TotalBytes()
    {
        lock (Trava) return Ativos.Sum(f => f.SizeBytes);
    }

    /// <summary>Tamanho legível: "38 KB", "1,2 MB".</summary>
    /// <summary>
    /// Bloco que apresenta ao modelo os arquivos que o USUÁRIO anexou. Vazio quando não há
    /// nenhum, e nesse caso nada é inserido no prompt.
    /// <para>
    /// Só o caminho, nunca o conteúdo. Embutir o arquivo garantiria que o modelo o visse, mas
    /// uma planilha de 240 KB não cabe na janela e a lista aceita até
    /// <see cref="MaxRecentes"/> itens. Com o caminho literal na mão, ele chama
    /// <c>read</c> quando precisar — e só do que precisar.
    /// </para>
    /// <para>
    /// Só os anexados pelo usuário. O que a IA leu ou escreveu já está no histórico da
    /// conversa; repetir aqui seria pagar duas vezes pela mesma informação.
    /// </para>
    /// </summary>
    public static string RenderizarAnexados()
    {
        var anexados = Ativos.Where(a => a.Origin == ContextOrigin.AttachedByUser).ToList();
        if (anexados.Count == 0) return "";

        var texto = new System.Text.StringBuilder();
        texto.AppendLine("Arquivos que o usuário anexou a esta conversa:");

        foreach (var arquivo in anexados)
            texto.AppendLine($"- {arquivo.FilePath}");

        texto.Append(
            "Use read com o caminho exato acima quando o usuário se referir a um deles "
            + "pelo nome. Não os leia sem necessidade.");

        return texto.ToString();
    }

    public static string Humanizar(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    /// <summary>
    /// Caminho absoluto, sem barra final. Caminho inválido devolve vazio em vez de lançar: um
    /// argumento estranho vindo do modelo não pode derrubar a lista.
    /// </summary>
    private static string Normalizar(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return "";
        }
    }
}
