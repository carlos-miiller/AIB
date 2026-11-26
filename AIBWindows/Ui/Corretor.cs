using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using AIB.Services;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIB.Ui;

/// <summary>
/// O corretor ortográfico de uma caixa de texto: a da conversa e a do orbe.
/// <para>
/// Pedido do usuário (24/11/2026): sublinhar a palavra errada e, no botão direito em cima dela,
/// mostrar as sugestões e "Adicionar ao dicionário". Usa o corretor do próprio Windows (o
/// <see cref="SpellCheck"/> do WPF): sem rede, sem modelo, sem custo por mensagem. Em 26/11 o
/// usuário viu que o orbe ficou sem: nasceu dentro da ChatWindow, e por isso virou classe.
/// </para>
/// <para>
/// O dicionário do usuário é um .lex em <c>~/.AIB</c>, uma palavra por linha, comum às caixas:
/// a palavra adicionada numa deixa de ser sublinhada na outra também. O menu do WPF não tem
/// "adicionar", por isso o menu é montado aqui, no estilo do tema.
/// </para>
/// </summary>
public sealed class Corretor
{
    /// <summary>Alguma caixa gravou palavra nova: as outras recarregam o dicionário.</summary>
    private static event Action<string>? DicionarioMudou;

    private readonly TextBox _caixa;
    private string _dicionario = Path.Combine(DirectoryService.DataDir, "dicionario.lex");

    private Corretor(TextBox caixa) => _caixa = caixa;

    public static Corretor Ligar(TextBox caixa)
    {
        var c = new Corretor(caixa);
        caixa.Language = XmlLanguage.GetLanguage("pt-BR");
        SpellCheck.SetIsEnabled(caixa, true);
        c.Registrar();

        // Com o ContextMenu nulo, o TextBox abre o menu padrão dele, sem o "adicionar".
        caixa.ContextMenu = new ContextMenu();
        caixa.ContextMenuOpening += c.AbrirMenu;

        // Sem desligar: as duas janelas nascem uma vez e vivem o tempo do app.
        DicionarioMudou += caminho =>
        {
            if (caminho == c._dicionario) c.Recarregar();
        };
        return c;
    }

    /// <summary>O arquivo das palavras que o usuário adicionou. Os ensaios trocam por um temporário.</summary>
    public string Dicionario
    {
        get => _dicionario;
        set
        {
            Desregistrar();
            _dicionario = value;
            Registrar();
        }
    }

    private void AbrirMenu(object sender, ContextMenuEventArgs e)
    {
        // Pelo teclado (Shift+F10) não há ponto: vale a palavra do cursor.
        int i = e.CursorLeft < 0
            ? _caixa.CaretIndex
            : _caixa.GetCharacterIndexFromPoint(Mouse.GetPosition(_caixa), false);
        var erro = i >= 0 ? _caixa.GetSpellingError(i) : null;
        MontarMenu(erro, erro == null ? "" : PalavraEm(i));
    }

    /// <summary>Sugestões e "adicionar" quando há erro no ponto; recortar, copiar e colar sempre.</summary>
    public void MontarMenu(SpellingError? erro, string palavra)
    {
        var menu = _caixa.ContextMenu!;
        menu.Items.Clear();

        if (erro != null)
        {
            var sugestoes = erro.Suggestions.Take(5).ToList();
            foreach (var s in sugestoes)
            {
                var item = new MenuItem { Header = s, FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => erro.Correct(s);
                menu.Items.Add(item);
            }
            if (sugestoes.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Sem sugestões", IsEnabled = false });

            var adicionar = new MenuItem { Header = "Adicionar ao dicionário" };
            adicionar.Click += (_, _) => Adicionar(palavra);
            menu.Items.Add(new Separator());
            menu.Items.Add(adicionar);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Comando("Recortar", ApplicationCommands.Cut));
        menu.Items.Add(Comando("Copiar", ApplicationCommands.Copy));
        menu.Items.Add(Comando("Colar", ApplicationCommands.Paste));
    }

    private MenuItem Comando(string nome, RoutedUICommand comando) =>
        new() { Header = nome, Command = comando, CommandTarget = _caixa };

    /// <summary>A palavra sublinhada que passa pelo índice.</summary>
    private string PalavraEm(int i)
    {
        int inicio = _caixa.GetSpellingErrorStart(i);
        int tamanho = _caixa.GetSpellingErrorLength(i);
        return inicio >= 0 && tamanho > 0 ? _caixa.Text.Substring(inicio, tamanho) : "";
    }

    /// <summary>
    /// Grava a palavra no .lex e recarrega o dicionário nas caixas: o WPF lê o arquivo ao
    /// registrar, então sem tirar e pôr de novo a palavra continuaria sublinhada.
    /// </summary>
    public void Adicionar(string palavra)
    {
        palavra = palavra.Trim();
        if (palavra.Length == 0 || palavra.Any(char.IsWhiteSpace)) return;

        var palavras = Palavras();
        if (palavras.Contains(palavra, StringComparer.Ordinal)) return;
        palavras.Add(palavra);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dicionario)!);
            File.WriteAllLines(_dicionario, palavras, Encoding.Unicode);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        DicionarioMudou?.Invoke(_dicionario);
    }

    private void Recarregar()
    {
        Desregistrar();
        Registrar();
    }

    private List<string> Palavras()
    {
        try
        {
            return File.Exists(_dicionario)
                ? File.ReadAllLines(_dicionario).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : new List<string>();
        }
        catch (IOException) { return new List<string>(); }
    }

    private Uri Endereco => new(_dicionario, UriKind.Absolute);

    private void Registrar()
    {
        if (File.Exists(_dicionario)) SpellCheck.GetCustomDictionaries(_caixa).Add(Endereco);
    }

    private void Desregistrar()
    {
        var lista = SpellCheck.GetCustomDictionaries(_caixa);
        foreach (var u in lista.OfType<Uri>().Where(u => u == Endereco).ToList()) lista.Remove(u);
    }
}
