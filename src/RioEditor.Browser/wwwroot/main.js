// Avalonia WebAssembly bootstrap. Starts the .NET runtime, then hands control to Program.Main.
import { dotnet } from './_framework/dotnet.js';

const isBrowser = typeof window !== 'undefined';
if (!isBrowser) {
  throw new Error('RioEditor must run in a browser.');
}

const runtime = await dotnet
  .withDiagnosticTracing(false)
  .withApplicationArgumentsFromQuery()
  .create();

// The splash sits above the canvas until Avalonia has painted its first frame.
const splash = document.getElementById('rio-splash');
if (splash) {
  setTimeout(() => splash.remove(), 400);
}

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
