using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Lightning.CLightning;
using BTCPayServer.Services;
using BTCPayServer.Tests.Logging;
using BTCPayServer.Views.Manage;
using BTCPayServer.Views.Server;
using BTCPayServer.Views.Stores;
using BTCPayServer.Views.Wallets;
using NBitcoin;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Firefox;
using Xunit;

namespace BTCPayServer.Tests
{
    public class SeleniumTester : IDisposable
    {
        public IWebDriver Driver { get; set; }
        public ServerTester Server { get; set; }
        public WalletId WalletId { get; set; }

        public string StoreId { get; set; }

        public static SeleniumTester Create([CallerMemberNameAttribute] string scope = null, bool newDb = false) =>
            new SeleniumTester { Server = ServerTester.Create(scope, newDb) };

        public async Task StartAsync()
        {
            await Server.StartAsync();
            var isDebug = !Server.PayTester.InContainer;
            var runHeadless = true; // Set to false to view in browser
            var useFirefox = !runHeadless;
            var windowSize = (Width: 1200, Height: 1000);

            if (useFirefox)
            {
                var options = new FirefoxOptions();
                if (runHeadless)
                {
                    options.AddArguments("-headless");
                }
                options.AddArguments($"--window-size {windowSize.Width},{windowSize.Height}");
                Driver = new FirefoxDriver(options);
            }
            else
            {
                var options = new ChromeOptions();
                if (Server.PayTester.InContainer)
                {
                    // this must be first option https://stackoverflow.com/questions/53073411/selenium-webdriverexceptionchrome-failed-to-start-crashed-as-google-chrome-is#comment102570662_53073789
                    options.AddArgument("no-sandbox");
                }
                if (runHeadless)
                {
                    options.AddArguments("-headless");
                }
                options.AddArguments($"window-size={windowSize.Width}x{windowSize.Height}");
                options.AddArgument("shm-size=2g");
                Driver = new ChromeDriver(Server.PayTester.InContainer ? "/usr/bin" : Directory.GetCurrentDirectory(), options);
            }

            if (!runHeadless)
            {
                // when running in the browser, depending on your resolution, the website
                // may go into mobile responsive mode and screw with navigation of tests
                Driver.Manage().Window.Maximize();
            }
            Logs.Tester.LogInformation($"Selenium: Using {Driver.GetType()}");
            Logs.Tester.LogInformation($"Selenium: Browsing to {Server.PayTester.ServerUri}");
            Logs.Tester.LogInformation($"Selenium: Resolution {Driver.Manage().Window.Size}");
            Driver.Manage().Timeouts().ImplicitWait = ImplicitWait;
            GoToRegister();
            Driver.AssertNoError();
        }

        internal IWebElement FindAlertMessage(StatusMessageModel.StatusSeverity severity = StatusMessageModel.StatusSeverity.Success) =>
            Driver.FindElement(By.ClassName($"alert-{StatusMessageModel.ToString(severity)}"));

        public static readonly TimeSpan ImplicitWait = TimeSpan.FromSeconds(5);
        public string Link(string relativeLink)
        {
            return Server.PayTester.ServerUri.AbsoluteUri.WithoutEndingSlash() + relativeLink.WithStartingSlash();
        }

        public void GoToRegister()
        {
            Driver.Navigate().GoToUrl(Link("/Account/Register"));
        }

        public string RegisterNewUser(bool isAdmin = false)
        {
            var usr = RandomUtils.GetUInt256().ToString().Substring(64 - 20) + "@a.com";
            Logs.Tester.LogInformation($"User: {usr} with password 123456");
            Driver.FindElement(By.Id("Email")).SendKeys(usr);
            Driver.FindElement(By.Id("Password")).SendKeys("123456");
            Driver.FindElement(By.Id("ConfirmPassword")).SendKeys("123456");
            if (isAdmin)
                Driver.FindElement(By.Id("IsAdmin")).Click();
            Driver.FindElement(By.Id("RegisterButton")).Click();
            Driver.AssertNoError();
            return usr;
        }

        public (string storeName, string storeId) CreateNewStore()
        {
            Driver.FindElement(By.Id("Stores")).Click();
            Driver.FindElement(By.Id("CreateStore")).Click();
            var name = "Store" + RandomUtils.GetUInt64();
            Driver.FindElement(By.Id("Name")).SendKeys(name);
            Driver.FindElement(By.Id("Create")).Click();
            StoreId = Driver.FindElement(By.Id("Id")).GetAttribute("value");
            return (name, StoreId);
        }

        public Mnemonic GenerateWallet(string cryptoCode = "BTC", string seed = "", bool importkeys = false, bool privkeys = false, ScriptPubKeyType format = ScriptPubKeyType.Segwit)
        {
            Driver.FindElement(By.Id($"Modify{cryptoCode}")).Click();
            Driver.FindElement(By.Id("import-from-btn")).Click();
            Driver.FindElement(By.Id("nbxplorergeneratewalletbtn")).Click();
            Driver.FindElement(By.Id("ExistingMnemonic")).SendKeys(seed);
            SetCheckbox(Driver.FindElement(By.Id("SavePrivateKeys")), privkeys);
            SetCheckbox(Driver.FindElement(By.Id("ImportKeysToRPC")), importkeys);
            Driver.FindElement(By.Id("ScriptPubKeyType")).Click();
            Driver.FindElement(By.CssSelector($"#ScriptPubKeyType option[value={format}]")).Click();
            Logs.Tester.LogInformation("Trying to click btn-generate");
            Driver.FindElement(By.Id("btn-generate")).Click();
            // Seed backup page
            FindAlertMessage();
            if (string.IsNullOrEmpty(seed))
            {
                seed = Driver.FindElements(By.Id("recovery-phrase")).First().GetAttribute("data-mnemonic");
            }
            // Confirm seed backup
            Driver.FindElement(By.Id("confirm")).Click();
            Driver.FindElement(By.Id("submit")).Click();

            WalletId = new WalletId(StoreId, cryptoCode);
            return new Mnemonic(seed);
        }

        public void AddDerivationScheme(string cryptoCode = "BTC", string derivationScheme = "xpub661MyMwAqRbcGABgHMUXDzPzH1tU7eZaAaJQXhDXsSxsqyQzQeU6kznNfSuAyqAK9UaWSaZaMFdNiY5BCF4zBPAzSnwfUAwUhwttuAKwfRX-[legacy]")
        {
            Driver.FindElement(By.Id($"Modify{cryptoCode}")).Click();
            Driver.FindElement(By.ClassName("store-derivation-scheme")).SendKeys(derivationScheme);
            Driver.FindElement(By.Id("Continue")).Click();
            Driver.FindElement(By.Id("Confirm")).Click();
            FindAlertMessage();
        }

        public void AddLightningNode(string cryptoCode, LightningConnectionType connectionType)
        {
            string connectionString;
            if (connectionType == LightningConnectionType.Charge)
                connectionString = $"type=charge;server={Server.MerchantCharge.Client.Uri.AbsoluteUri};allowinsecure=true";
            else if (connectionType == LightningConnectionType.CLightning)
                connectionString = "type=clightning;server=" + ((CLightningClient)Server.MerchantLightningD).Address.AbsoluteUri;
            else if (connectionType == LightningConnectionType.LndREST)
                connectionString = $"type=lnd-rest;server={Server.MerchantLnd.Swagger.BaseUrl};allowinsecure=true";
            else
                throw new NotSupportedException(connectionType.ToString());

            Driver.FindElement(By.Id($"Modify-Lightning{cryptoCode}")).Click();
            Driver.FindElement(By.Name($"ConnectionString")).SendKeys(connectionString);
            Driver.FindElement(By.Id($"save")).Click();
        }

        public void AddInternalLightningNode(string cryptoCode)
        {
            Driver.FindElement(By.Id($"Modify-Lightning{cryptoCode}")).Click();
            Driver.FindElement(By.Id($"internal-ln-node-setter")).Click();
            Driver.FindElement(By.Id($"save")).Click();
        }

        public void ClickOnAllSideMenus()
        {
            var links = Driver.FindElements(By.CssSelector(".nav-pills .nav-link")).Select(c => c.GetAttribute("href")).ToList();
            Driver.AssertNoError();
            Assert.NotEmpty(links);
            foreach (var l in links)
            {
                Logs.Tester.LogInformation($"Checking no error on {l}");
                Driver.Navigate().GoToUrl(l);
                Driver.AssertNoError();
            }
        }

        public void Dispose()
        {
            if (Driver != null)
            {
                try
                {
                    Driver.Quit();
                }
                catch
                {
                    // ignored
                }

                Driver.Dispose();
            }

            Server?.Dispose();
        }

        internal void AssertNotFound()
        {
            Assert.Contains("404 - Page not found</h1>", Driver.PageSource);
        }

        public void GoToHome()
        {
            Driver.Navigate().GoToUrl(Server.PayTester.ServerUri);
        }

        public void Logout()
        {
            Driver.FindElement(By.Id("Logout")).Click();
        }

        public void Login(string user, string password)
        {
            Driver.FindElement(By.Id("Email")).SendKeys(user);
            Driver.FindElement(By.Id("Password")).SendKeys(password);
            Driver.FindElement(By.Id("LoginButton")).Click();
        }

        public void GoToStores()
        {
            Driver.FindElement(By.Id("Stores")).Click();
        }

        public void GoToStore(string storeId, StoreNavPages storeNavPage = StoreNavPages.Index)
        {
            Driver.FindElement(By.Id("Stores")).Click();
            Driver.FindElement(By.Id($"update-store-{storeId}")).Click();

            if (storeNavPage != StoreNavPages.Index)
            {
                Driver.FindElement(By.Id(storeNavPage.ToString())).Click();
            }
        }

        public void GoToInvoiceCheckout(string invoiceId)
        {
            Driver.FindElement(By.Id("Invoices")).Click();
            Driver.FindElement(By.Id($"invoice-checkout-{invoiceId}")).Click();
            CheckForJSErrors();
        }


        public void SetCheckbox(IWebElement element, bool value)
        {
            if ((value && !element.Selected) || (!value && element.Selected))
            {
                element.Click();
            }

            if (value != element.Selected)
            {
                Logs.Tester.LogInformation("SetCheckbox recursion, trying to click again");
                SetCheckbox(element, value);
            }
        }

        public void SetCheckbox(SeleniumTester s, string checkboxId, bool value)
        {
            SetCheckbox(s.Driver.FindElement(By.Id(checkboxId)), value);
        }

        public void GoToInvoices()
        {
            Driver.FindElement(By.Id("Invoices")).Click();
        }

        public void GoToProfile(ManageNavPages navPages = ManageNavPages.Index)
        {
            Driver.FindElement(By.Id("MySettings")).Click();
            if (navPages != ManageNavPages.Index)
            {
                Driver.FindElement(By.Id(navPages.ToString())).Click();
            }
        }

        public void GoToLogin()
        {
            Driver.Navigate().GoToUrl(new Uri(Server.PayTester.ServerUri, "Account/Login"));
        }

        public string CreateInvoice(string storeName, decimal amount = 100, string currency = "USD", string refundEmail = "")
        {
            GoToInvoices();
            Driver.FindElement(By.Id("CreateNewInvoice")).Click();
            Driver.FindElement(By.Id("Amount")).SendKeys(amount.ToString(CultureInfo.InvariantCulture));
            var currencyEl = Driver.FindElement(By.Id("Currency"));
            currencyEl.Clear();
            currencyEl.SendKeys(currency);
            Driver.FindElement(By.Id("BuyerEmail")).SendKeys(refundEmail);
            Driver.FindElement(By.Name("StoreId")).SendKeys(storeName);
            Driver.FindElement(By.Id("Create")).Click();

            FindAlertMessage();
            var statusElement = Driver.FindElement(By.ClassName("alert-success"));
            var id = statusElement.Text.Split(" ")[1];
            return id;
        }

        public async Task FundStoreWallet(WalletId walletId = null, int coins = 1, decimal denomination = 1m)
        {
            walletId ??= WalletId;
            GoToWallet(walletId, WalletsNavPages.Receive);
            Driver.FindElement(By.Id("generateButton")).Click();
            var addressStr = Driver.FindElement(By.Id("address")).GetProperty("value");
            var address = BitcoinAddress.Create(addressStr, ((BTCPayNetwork)Server.NetworkProvider.GetNetwork(walletId.CryptoCode)).NBitcoinNetwork);
            for (var i = 0; i < coins; i++)
            {
                await Server.ExplorerNode.SendToAddressAsync(address, Money.Coins(denomination));
            }
        }

        public void PayInvoice(WalletId walletId, string invoiceId)
        {
            GoToInvoiceCheckout(invoiceId);
            var bip21 = Driver.FindElement(By.ClassName("payment__details__instruction__open-wallet__btn"))
                .GetAttribute("href");
            Assert.Contains($"{PayjoinClient.BIP21EndpointKey}", bip21);

            GoToWallet(walletId);
            Driver.FindElement(By.Id("bip21parse")).Click();
            Driver.SwitchTo().Alert().SendKeys(bip21);
            Driver.SwitchTo().Alert().Accept();
            Driver.FindElement(By.Id("SendMenu")).Click();
            Driver.FindElement(By.CssSelector("button[value=nbx-seed]")).Click();
            Driver.FindElement(By.CssSelector("button[value=broadcast]")).Click();
        }

        private void CheckForJSErrors()
        {
            //wait for seleniun update: https://stackoverflow.com/questions/57520296/selenium-webdriver-3-141-0-driver-manage-logs-availablelogtypes-throwing-syste
            //            var errorStrings = new List<string> 
            //            { 
            //                "SyntaxError", 
            //                "EvalError", 
            //                "ReferenceError", 
            //                "RangeError", 
            //                "TypeError", 
            //                "URIError" 
            //            };
            //
            //            var jsErrors = Driver.Manage().Logs.GetLog(LogType.Browser).Where(x => errorStrings.Any(e => x.Message.Contains(e)));
            //
            //            if (jsErrors.Any())
            //            {
            //                Logs.Tester.LogInformation("JavaScript error(s):" + Environment.NewLine + jsErrors.Aggregate("", (s, entry) => s + entry.Message + Environment.NewLine));
            //            }
            //            Assert.Empty(jsErrors);

        }

        public void GoToWallet(WalletId walletId = null, WalletsNavPages navPages = WalletsNavPages.Send)
        {
            walletId ??= WalletId;
            Driver.Navigate().GoToUrl(new Uri(Server.PayTester.ServerUri, $"wallets/{walletId}"));
            if (navPages != WalletsNavPages.Transactions)
            {
                Driver.FindElement(By.Id($"Wallet{navPages}")).Click();
            }
        }

        public void GoToUrl(string relativeUrl)
        {
            Driver.Navigate().GoToUrl(new Uri(Server.PayTester.ServerUri, relativeUrl));
        }
        
        public void GoToServer(ServerNavPages navPages = ServerNavPages.Index)
        {
            Driver.FindElement(By.Id("ServerSettings")).Click();
            if (navPages != ServerNavPages.Index)
            {
                Driver.FindElement(By.Id($"Server-{navPages}")).Click();
            }
        }

        public void GoToInvoice(string id)
        {
            GoToInvoices();
            foreach (var el in Driver.FindElements(By.ClassName("invoice-details-link")))
            {
                if (el.GetAttribute("href").Contains(id, StringComparison.OrdinalIgnoreCase))
                {
                    el.Click();
                    break;
                }
            }
        }
    }
}
