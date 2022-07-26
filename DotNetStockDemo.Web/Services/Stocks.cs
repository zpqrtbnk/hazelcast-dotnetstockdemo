namespace DotNetStockDemo.Web.Services;


public static class Stocks
{
    public class Stock
    {
        public Stock(string ticker, string name, float cap)
        {
            Ticker = ticker;
            Name = name;
            Cap = cap;
        }

        public string Ticker { get; set; }

        public string Name { get; set; }

        public float Cap { get; set; }
    }

    public static Stock[] All = 
    {
        new("GOOG", "Google", 1.2345f),
        new("APPL", "Apple", 7.65432f),
    };
}