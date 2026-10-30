namespace LinkGate.Review;

public interface IReviewTerminal
{
    void WriteLine(string text);
    string? ReadLine();
}

public sealed class ReviewInputException() : Exception("Invalid review command.");

public sealed class TextReviewTerminal(TextReader input,TextWriter output) : IReviewTerminal
{
    public void WriteLine(string text)=>output.WriteLine(text);
    public string? ReadLine()
    {
        var line=new System.Text.StringBuilder(128);
        bool carriage=false;
        while(true)
        {
            int value=input.Read();
            if(value<0)return line.Length==0&&!carriage?null:throw new ReviewInputException();
            if(value=='\n')return line.ToString();
            if(value=='\r'&&!carriage){carriage=true;continue;}
            if(carriage || value<32 || value>126 || line.Length==128)throw new ReviewInputException();
            line.Append((char)value);
        }
    }
}
