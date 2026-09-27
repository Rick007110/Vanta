try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected or legacy console */ }
return Vanta.Core.Cli.Run(args, Console.Out);
