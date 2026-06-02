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

    /// <summary>
    /// D-04 (Phase 3): veredito do precheck do floor list para <c>run_command</c>.
    /// Quando <c>true</c>, o modal renderiza um banner AVISO amber abaixo do
    /// CommandText alertando que a aprovação será refutada pelo floor post-modal.
    /// Para skills (execute_skill), permanece <c>false</c>.
    /// </summary>
    public bool DenylistHit { get; init; } = false;

    /// <summary>
    /// D-04 (Phase 3): razão PT-BR user-visible do floor list. Renderizada como
    /// texto do banner AVISO no modal quando <see cref="DenylistHit"/> é true.
    /// Vem de <c>CommandFloorList.Match(...)</c>.
    /// </summary>
    public string? DenylistReason { get; init; }

    /// <summary>D-08: full or 50KB-capped script body to display in the modal preview row.
    /// Null for run_command (command-line-only mode).</summary>
    public string? ScriptBody { get; init; }

    /// <summary>D-08: interpreter label (python / powershell / cmd) shown in the header
    /// when ScriptBody is non-null. Null for run_command.</summary>
    public string? Interpreter { get; init; }

    /// <summary>D-08 + D-10: SHA256 hex digest of the script content; null for run_command.
    /// Also forms the third element of the AlwaysAllowSession tuple key so silent disk edits
    /// invalidate the cache.</summary>
    public string? ContentHash { get; init; }
}
