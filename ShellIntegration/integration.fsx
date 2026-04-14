// splash F# Interactive integration. Intentionally empty: there is
// nothing this script can do that doesn't surface as visible output
// in the REPL (every top-level statement in F# Interactive emits
// `val it: ... = <result>`, which the regex-based prompt tracker
// would mis-resolve as the first real command's output). The
// existence of the script and the `--use:` flag are kept so the
// command_template can pass a non-empty argument and so a future
// adapter version can grow integration logic without changing the
// process spec.
//
// Why pass --use: at all? `dotnet fsi` started under ConPTY without
// a script argument exits within ~80ms before the first prompt is
// drawn (host detection edge case in the dotnet launcher). Passing
// any --use: file keeps the REPL alive long enough for splash to
// see its first prompt.
