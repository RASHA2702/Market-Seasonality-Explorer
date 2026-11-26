using FiatCryptoGateway.ReviewerSetup;

Console.OutputEncoding = System.Text.Encoding.UTF8;
return new ReviewerSetupFlow(new ConsoleTerminal()).Run(args);
