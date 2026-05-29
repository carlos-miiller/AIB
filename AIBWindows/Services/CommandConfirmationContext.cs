namespace AIB.Services;

/// <summary>
/// Payload imutável passado de RunCommandTool para CommandConfirmationWindow
/// quando uma chamada de ferramenta precisa de aprovação humana.
///
/// Carrega exatamente os quatro campos exigidos pelo D3 do fase 01:
/// nome da ferramenta, texto do comando, nível do usuário e o diretório
/// de trabalho onde cmd.exe rodará. Sem comportamento — só dados.
/// </summary>
public class CommandConfirmationContext
{
    public string Tool { get; init; } = "";
    public string Command { get; init; } = "";
    public int Level { get; init; } = 0;
    public string Cwd { get; init; } = "";
}
