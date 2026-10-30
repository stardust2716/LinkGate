using LinkGate.Broker;
using LinkGate.Review;

if(args.Length!=1){Console.Error.WriteLine("Usage: LinkGate.Review.Console <existing-private-state-directory>");return 2;}
if(Console.IsInputRedirected || Console.IsOutputRedirected || !Environment.UserInteractive)
{Console.Error.WriteLine("Interactive input and output required; state was not opened.");return 2;}
try
{
    string directory=Path.GetFullPath(args[0]);
    if(!Directory.Exists(directory) || !File.Exists(Path.Combine(directory,"registration.db")))
    {Console.Error.WriteLine("Existing development registration database required.");return 2;}
    Console.OutputEncoding=System.Text.Encoding.UTF8;
    var terminal=new TextReviewTerminal(Console.In,Console.Out);
    terminal.WriteLine(ReviewText.Boundary);
    terminal.WriteLine("将打开指定的开发状态目录，必要时迁移数据库。输入 OPEN 继续；其他输入取消。");
    if(terminal.ReadLine()!="OPEN")return 0;
    var store=new RegistrationStore(directory);
    using var journal=new ReviewIntentFile(directory);
    return new ReviewWorkflow(store,terminal,journal).Run();
}
catch(Exception){Console.Error.WriteLine("Review console could not open or complete the operation; no automatic replay.");return 1;}
