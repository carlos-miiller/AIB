using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using AIB.Services.Mail;

namespace AIB.Ui;

/// <summary>
/// Traduz o estado de uma caixa de e-mail para o que a linha de §3.12 precisa desenhar.
/// <para>
/// Um conversor com modo, e não três classes, pelo mesmo motivo do
/// <see cref="UrgenciaConverter"/>: a cor do ponto e o texto do ToolTip descrevem o MESMO
/// estado, e separá-los abre a porta para eles discordarem — ponto verde com ToolTip
/// "Falha de login" é o tipo de defeito que ninguém percebe até um usuário reclamar.
/// </para>
/// </summary>
public sealed class ContaDeEmailConverter : IValueConverter
{
    public enum Saida
    {
        /// <summary>Cor do ponto de estado da coluna 0.</summary>
        CorDoPonto,

        /// <summary>ToolTip do ponto, verbatim de §3.12.</summary>
        RotuloDoPonto,

        /// <summary>Pincel do texto de estado: cinza normalmente, vermelho em erro.</summary>
        CorDoTexto,

        /// <summary>Visible quando é a conta principal — o selo.</summary>
        VisibilidadeDoSelo,

        /// <summary>Visible quando NÃO é a principal — o botão de estrela.</summary>
        VisibilidadeDaEstrela
    }

    public Saida Modo { get; set; } = Saida.CorDoPonto;

    // Congelados: são criados uma vez e usados em toda linha da lista. Verde e âmbar ficam em
    // hex porque não há token com a MESMA cor (SuccessBrush e WarnBrush são outros tons), e
    // trocá-los mudaria o ponto de estado.
    private static readonly SolidColorBrush Verde = Congelar(0x3F, 0xBF, 0x7F);
    private static readonly SolidColorBrush Ambar = Congelar(0xE2, 0xA0, 0x3F);

    // Estes três SÃO tokens de Themes/Tokens.xaml, e vêm de lá — ver PincelDoTema. Lidos a cada
    // uso, e não num campo estático: o campo seria preenchido na primeira linha desenhada, e
    // se ela viesse antes do dicionário carregado a reserva ficaria para sempre.
    private static SolidColorBrush Vermelho => PincelDoTema.De("DangerBrush", "#FFE5484D");
    private static SolidColorBrush Cinza => PincelDoTema.De("TextMutedBrush", "#FF6F6A7A");
    private static SolidColorBrush VermelhoDeTexto => PincelDoTema.De("DangerTextBrush", "#FFFF8A8D");

    private static SolidColorBrush Congelar(byte r, byte g, byte b)
    {
        var pincel = new SolidColorBrush(Color.FromRgb(r, g, b));
        pincel.Freeze();
        return pincel;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (Modo is Saida.VisibilidadeDoSelo or Saida.VisibilidadeDaEstrela)
        {
            bool principal = value is bool b && b;
            bool mostrar = Modo == Saida.VisibilidadeDoSelo ? principal : !principal;
            return mostrar ? Visibility.Visible : Visibility.Collapsed;
        }

        var estado = value is MailAccountStatus s ? s : MailAccountStatus.Checking;

        return Modo switch
        {
            Saida.CorDoPonto => estado switch
            {
                MailAccountStatus.Ok => Verde,
                MailAccountStatus.Error => Vermelho,
                _ => Ambar
            },

            Saida.RotuloDoPonto => estado switch
            {
                MailAccountStatus.Ok => "Conectada",
                MailAccountStatus.Error => "Falha de login",
                _ => "Verificando…"
            },

            Saida.CorDoTexto => estado == MailAccountStatus.Error ? VermelhoDeTexto : Cinza,

            _ => Cinza
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("mão única: o estado da conta vem do serviço, não da tela");
}
