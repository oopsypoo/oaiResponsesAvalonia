 # Todo List #
 
* Git - show read-only repository status beneath the Workspace root field:
  * Current branch (or detached HEAD), working-tree changes, and ahead/behind status relative to the configured upstream when available.
  * Clearly indicate when there is no Git repository or no upstream; note that ahead/behind data reflects the last fetch.
  * Do not perform Git operations that modify the repository or remote; the user will use another application for those operations.
  * Decide whether to obtain status via the Git CLI or a library.
* Developer Tools ->
  * Define the intended behavior and access boundaries for the Scope options (Current repository, Workspace, Custom), then re-enable the hidden Scope control. `SelectedValueBinding="{Binding Tag}"` is configured to bind each option to `DeveloperScope`.
* Terminal_diagnostics_tool
  * [x] in sessions: missing a tool/command/filter to find/see what a session/conversation inhibits of tools or media
  * [x] it should be easy to find out if a session has a property or incorporates a certain tool
