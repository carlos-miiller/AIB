using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AIB.Services.Documentos;

/// <summary>
/// Os modelos de documento (<c>action='models'</c>): planilhas e Word bem acabados para a Ellen
/// partir deles em vez de montar do zero.
/// <para>
/// Pedido do usuário, 26/10/2026, depois de comparar o painel que a Ellen montou com um modelo de
/// rubrica: dar a ela referências. Os que vêm com o AIB ficam na pasta do programa
/// (<c>modelos/</c>); os do usuário em <c>~/.AIB/modelos</c>, onde ele põe o que quiser. A
/// descrição de cada um é o arquivo <c>.txt</c> de mesmo nome, quando há.
/// </para>
/// </summary>
public sealed class ModelosDoDoc
{
    private static readonly string[] Extensoes = { ".xlsx", ".xlsm", ".docx" };

    private readonly string _doPrograma;
    private readonly string _doUsuario;

    /// <param name="doPrograma">Os que vêm com o AIB. Nulo: a pasta <c>modelos</c> ao lado do executável.</param>
    /// <param name="doUsuario">Os do usuário. Nulo: <c>~/.AIB/modelos</c>.</param>
    public ModelosDoDoc(string? doPrograma = null, string? doUsuario = null)
    {
        _doPrograma = doPrograma ?? Path.Combine(AppContext.BaseDirectory, "modelos");
        _doUsuario = doUsuario ?? DirectoryService.ModelosDir;
    }

    public sealed record Modelo(string Nome, string Caminho, bool DoUsuario, string? Descricao);

    public IReadOnlyList<Modelo> Listar()
    {
        var lista = new List<Modelo>();
        Juntar(_doUsuario, true, lista);
        Juntar(_doPrograma, false, lista);
        return lista;
    }

    private static void Juntar(string pasta, bool doUsuario, List<Modelo> lista)
    {
        if (!Directory.Exists(pasta)) return;
        IEnumerable<string> arquivos;
        try { arquivos = Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories).ToList(); }
        catch { return; }

        foreach (var arq in arquivos.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(arq).ToLowerInvariant();
            string nome = Path.GetFileName(arq);
            // ~$ é o arquivo de trava que o Excel e o Word deixam ao lado do aberto.
            if (!Extensoes.Contains(ext) || nome.StartsWith("~$", StringComparison.Ordinal)) continue;
            lista.Add(new Modelo(Path.GetFileNameWithoutExtension(arq), arq, doUsuario, Descricao(arq)));
        }
    }

    private static string? Descricao(string arquivo)
    {
        string txt = Path.ChangeExtension(arquivo, ".txt");
        try
        {
            if (!File.Exists(txt)) return null;
            string t = File.ReadAllText(txt).Trim();
            return t.Length == 0 ? null : Apresentacao.Curto(Apresentacao.Achatar(t), 400);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>create</c> com <c>from</c> de um Word: cópia exata do arquivo (sem ops — editar um Word é
    /// controle de alterações, e num documento recém-nascido isso só atrapalharia). Planilha passa
    /// por <see cref="EditorDePlanilha.Planejar"/>, que aceita ops sobre o modelo.
    /// </summary>
    public static Gravacao Copiar(string destino, string modelo)
    {
        bool existe = File.Exists(destino);
        string nome = Path.GetFileName(modelo);
        var plano = new PlanoDeGravacao
        {
            Arquivo = Path.GetFullPath(destino),
            Criar = !existe,
            Titulo = existe ? "Substituir este documento por uma cópia do modelo?" : "Criar este documento a partir do modelo?",
            Consequencia = (existe ? "O arquivo existe: TODO o conteúdo atual é trocado por uma cópia de " : "Arquivo novo, cópia de ")
                + $"'{nome}'. O modelo não muda.",
            Resumo = $"modelo {nome} · {new FileInfo(modelo).Length / 1024 + 1} KB",
            Copia = existe ? CopiasDeDocumento.Descricao(destino) : null,
            Impressao = existe ? Gravacao.Impressao(destino) : null
        };
        return new CopiaDeModelo(plano, modelo, $"documento criado a partir do modelo '{nome}'");
    }

    private sealed class CopiaDeModelo : Gravacao
    {
        private readonly string _modelo;

        public CopiaDeModelo(PlanoDeGravacao plano, string modelo, string resultado) : base(plano)
        {
            _modelo = modelo;
            Resultado = resultado;
        }

        public override string Resultado { get; }

        protected override void GravarEm(string caminho) => File.Copy(_modelo, caminho, overwrite: true);
    }

    /// <summary>A resposta de action='models'.</summary>
    public string Texto()
    {
        var lista = Listar();
        var sb = new StringBuilder();

        if (lista.Count == 0)
        {
            sb.AppendLine("Nenhum modelo encontrado.");
        }
        else
        {
            sb.AppendLine($"MODELOS ({lista.Count}). Antes de montar uma planilha do zero, veja se um destes serve de base ou de referência:");
            foreach (var m in lista)
            {
                sb.AppendLine();
                sb.Append("- ").Append(m.Nome).Append(m.DoUsuario ? " (do usuário)" : " (do AIB)").AppendLine();
                if (m.Descricao != null) sb.Append("  ").AppendLine(m.Descricao);
                sb.Append("  path: ").AppendLine(m.Caminho);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Como usar:");
        sb.AppendLine("- Ver como é feito: action='layout' com o path do modelo (e 'sheet' para outra aba). Devolve as ops que reproduzem o visual.");
        sb.AppendLine("- Ver o conteúdo: action='info' e action='read', como em qualquer arquivo.");
        sb.AppendLine("- Começar dele: action='create', path=<arquivo novo>, from=<path do modelo>. Planilha aceita também 'ops' no mesmo pedido,");
        sb.AppendLine("  para já trocar os textos e preencher os dados (um cartão só). O modelo não muda.");
        sb.AppendLine($"- O usuário põe os modelos dele em {_doUsuario} (.xlsx, .xlsm, .docx; um .txt de mesmo nome descreve o modelo).");
        return sb.ToString().TrimEnd();
    }
}
