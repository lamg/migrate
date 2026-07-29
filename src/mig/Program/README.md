# Program

CLI entrypoint lives in `../Program.fs`.

Argument parsing is hand-written (no Argu) for Native AOT friendliness:

- `mig version`
- `mig codegen -m <dir> -o <dir> -n <namespace>`
- `mig help`

All user-facing output uses `Console.WriteLine` / `Console.Error.WriteLine` (not F# `printfn`).
