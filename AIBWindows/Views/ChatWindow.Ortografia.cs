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

namespace AIB.Views;

/// <summary>
/// O corretor ortográfico da caixa de mensagem.
/// <para>
/// Pedido do usuário (24/11/2026): sublinhar a palavra errada e, no botão direito em cima dela,
/// mostrar as sugestões e "Adicionar ao dicionário". Usa o corretor do próprio Windows (o
/// <see cref="SpellCheck"/> do WPF): sem rede, sem modelo, sem custo por mensagem.
/// </para>
/// <para>
/// O dicionário do usuário é um .lex em <c>~/.AIB</c>, uma palavra por linha. O menu do WPF não
/// tem "adicionar", por isso o menu é montado aqui, no estilo do tema.
/// </para>
/// </summary>
public partial class ChatWindow
{
    private string _dicionario = Path.Combine(DirectoryService.DataDir, "dicionario.lex");

    /// <summary>O arquivo das palavras que o usuário adicionou. Os ensaios trocam por um temporário.</summary>
    internal string Dicionario
    {
        get => _dicionario;
        set
        {
            Desregistrar();
            _dicionario = value;
            Registrar();
        }
    }

    private void LigarOrtografia()
    {
        InputBox.Language = XmlLanguage.GetLanguage("pt-BR");
        SpellCheck.SetIsEnabled(InputBox, true);
        Registrar();

        // Com o ContextMenu nulo, o TextBox abre o menu padrão dele, sem o "adicionar".
        InputBox.ContextMenu = new ContextMenu();
        InputBox.ContextMenuOpening += AbrirMenuDaCaixa;
    }

    private void AbrirMenuDaCaixa(object sender, ContextMenuEventArgs e)
    {
        // Pelo teclado (Shift+F10) não há ponto: vale a palavra do cursor.
        int i = e.CursorLeft < 0
            ? InputBox.CaretIndex
            : InputBox.GetCharacterIndexFromPoint(Mouse.GetPosition(InputBox), false);
        var erro = i >= 0 ? InputBox.GetSpellingError(i) : null;
        MontarMenu(erro, erro == null ? "" : PalavraEm(i));
    }

    /// <summary>Sugestões e "adicionar" quando há erro no ponto; recortar, copiar e colar sempre.</summary>
    internal void MontarMenu(SpellingError? erro, string palavra)
    {
        var menu = InputBox.ContextMenu!;
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
            adicionar.Click += (_, _) => AdicionarAoDicionario(palavra);
            menu.Items.Add(new Separator());
            menu.Items.Add(adicionar);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Comando("Recortar", ApplicationCommands.Cut));
        menu.Items.Add(Comando("Copiar", ApplicationCommands.Copy));
        menu.Items.Add(Comando("Colar", ApplicationCommands.Paste));
    }

    private MenuItem Comando(string nome, RoutedUICommand comando) =>
        new() { Header = nome, Command = comando, CommandTarget = InputBox };

    /// <summary>A palavra sublinhada que passa pelo índice.</summary>
    private string PalavraEm(int i)
    {
        int inicio = InputBox.GetSpellingErrorStart(i);
        int tamanho = InputBox.GetSpellingErrorLength(i);
        return inicio >= 0 && tamanho > 0 ? InputBox.Text.Substring(inicio, tamanho) : "";
    }

    /// <summary>
    /// Grava a palavra no .lex e recarrega o dicionário na caixa: o WPF lê o arquivo ao
    /// registrar, então sem tirar e pôr de novo a palavra continuaria sublinhada.
    /// </summary>
    internal void AdicionarAoDicionario(string palavra)
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
        if (File.Exists(_dicionario)) SpellCheck.GetCustomDictionaries(InputBox).Add(Endereco);
    }

    private void Desregistrar()
    {
        var lista = SpellCheck.GetCustomDictionaries(InputBox);
        foreach (var u in lista.OfType<Uri>().Where(u => u == Endereco).ToList()) lista.Remove(u);
    }
}
