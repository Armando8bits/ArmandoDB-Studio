using System.Text;
using MySmdb;

// Acentos y caracteres no latinos tal cual, tanto en la terminal como al redirigir a un archivo o a otro programa.
Console.OutputEncoding = new UTF8Encoding(false);
if (Console.IsInputRedirected) Console.InputEncoding = new UTF8Encoding(false);

return await CliRunner.RunAsync(args, Console.Out, Console.Error, Console.In);
