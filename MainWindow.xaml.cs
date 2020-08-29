using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.ComponentModel;
using System.Timers;
using System.Threading;
using WooCommerceNET.WooCommerce.v3;
using WooCommerceNET.Base;
using System.Drawing;
using System.Configuration;

namespace EnsoNetSync
{
    /// <summary>
    /// The main logic of the app showing log entries to the user and launching frequent updates on a parallel thread.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Constants
        const string appVersion = "1.31";
        const int updateIntervalMin = 10;
        const int cacheLifespan = 20;
        const string logFile = "log.txt";

        // Handlers
        DBHandler DBHandler;
        WooHandler WooHandler;

        // Components
        System.Timers.Timer updateTimer;
        private readonly BackgroundWorker worker = new BackgroundWorker();

        // Fields
        List<Product> productsCache = new List<Product>();
        int cacheLife = 0;

        // Ctor
        public MainWindow()
        {
            InitializeComponent();

            Log("Initializing");

            this.Title += " " + appVersion;

            DBHandler = new DBHandler(ConfigurationManager.AppSettings["DbConn"]);
            WooHandler = new WooHandler(
                ConfigurationManager.AppSettings["WooUrl"],
                ConfigurationManager.AppSettings["WooRestCK"],
                ConfigurationManager.AppSettings["WooRestCS"],
                ConfigurationManager.AppSettings["WooRestAuth"],
                ConfigurationManager.AppSettings["WooRestCookie"]
                );
            DBHandler.logEvent += handler_logEvent;
            WooHandler.logEvent += handler_logEvent;

            worker.DoWork += worker_DoWork;

            updateTimer = new System.Timers.Timer(updateIntervalMin * 1000 * 60);
            updateTimer.AutoReset = true;
            updateTimer.Elapsed += updateTimer_Elapsed;

            Update();
        }

        // Logging
        private static ReaderWriterLock locker = new ReaderWriterLock();
        private void handler_logEvent(object sender, string e)
        {
            Log(e);
        }
        void Log(string text)
        {
            bool fileonly = false;
            if (text.StartsWith("#"))
            {
                text = text.Remove(0, 1);
                fileonly = true;
            }

            string line = "[" + DateTime.Now.ToString("yyyy.MM.dd HH:mm:ss.ff") + "] " + text + ";";

            // Add to UI
            if (!fileonly)
            {
                Dispatcher.Invoke(() =>
                {
                    if (textBox.Text.Length != 0) textBox.AppendText(Environment.NewLine);
                    textBox.AppendText(line);
                    textBox.Focus();
                    textBox.CaretIndex = textBox.Text.Length;
                    textBox.ScrollToEnd();
                });
            }

            // Write to file
            try
            {
                locker.AcquireWriterLock(3000);
                File.AppendAllLines(logFile, new string[] { line });
            }
            finally
            {
                locker.ReleaseWriterLock();
            }
        }

        // Update logic
        private void updateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            Update();
        }
        void Update()
        {
            if (!worker.IsBusy)
            {   
                worker.RunWorkerAsync();
            }
            else
            {
                Log("An update is already running!");
            }
        }
        private void worker_DoWork(object sender, DoWorkEventArgs e)
        {
            if (connectionsChecked || testConnections())
            {
                connectionsChecked = true;

                updateTimer.Stop();
                Dispatcher.Invoke(() => { forceButton.IsEnabled = false; });
                Log("▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬");
                Log("Update started!");

                syncProducts();

                updateTimer.Start();
                Dispatcher.Invoke(() => { forceButton.IsEnabled = true; });
                Log("Update finished! Next update at " + DateTime.Now.AddMinutes(updateIntervalMin));
            }
            else
            {
                Log("Connection testing failed. Check log, change config then restart the application!");
            }
        }
        private bool connectionsChecked = false;
        private bool testConnections()
        {
            Log("Testing connections");
            bool connDb = DBHandler.TestConn();
            Log("  DB connection: " + (connDb ? "OK" : "Failed"));
            bool connWp = WooHandler.TestConnWP();
            Log("  Wordpress connection: " + (connWp ? "OK" : "Failed"));
            bool connWoo = WooHandler.TestConnWoo();
            Log("  Woocommerce connection: " + (connWoo ? "OK" : "Failed"));
            return connDb && connWp && connWoo;
        }
        private void syncProducts()
        {
            Log("Syncing products");

            // Check if cache is empty or outdated
            if (cacheLife <= 0 || productsCache == null || productsCache.Count == 0)
            {
                Log("Cache is empty or outdated");
                if (productsCache == null) productsCache = new List<Product>();
                productsCache.Clear();

                // Refresh cache from woo
                Log("Downloading products from woocommerce");
                productsCache = WooHandler.GetAllProducts();
                if (productsCache == null || productsCache.Count == 0)
                { Log("CRITICAL ERROR: Could not retrieve products from woocommerce"); return; }
                Log("Downloaded " + productsCache.Count + " products from woocommerce");
                cacheLife = cacheLifespan;
            }
            else
            {
                // Using cache
                cacheLife--;
                Log("Using cache (" + productsCache.Count + " products) for updating. (" + cacheLife + " left)");
            }

            // Get products from enso
            Log("Downloading products from enso");
            List<Product> productsEnso = DBHandler.GetAllProducts();
            if (productsEnso == null) return;
            Log("Downloaded " + productsEnso.Count + " products from enso");

            // Fill batch with new products and changes
            ProductBatch productBatch = new ProductBatch();

            Log("Checking changes");
            foreach (Product ensoProd in productsEnso)
            {
                if (ensoProd == null || ensoProd.sku == null) { Log("ERROR: Enso product was null"); continue; }

                // Find existing product
                Product cacheProd = productsCache.Find(p => p.sku == ensoProd.sku);

                if (cacheProd == null) // ### NEW PRODUCT
                {
                    // Create new product
                    Log("  New product: (sku:" + ensoProd.sku + ") " + ensoProd.name);

                    // Set fields
                    ensoProd.status = "draft";
                    ensoProd.manage_stock = true;
                    ensoProd.backorders_allowed = false;

                    if (productBatch.create == null) productBatch.create = new List<Product>();

                    productBatch.create.Add(ensoProd);
                }
                else // ### EXISTING PRODUCT
                {
                    // Product already exists
                    if (cacheProd.id == null || cacheProd.sku == null)
                    { Log("ERROR: Missing product id:" + cacheProd.id + " or sku:" + cacheProd.sku); continue; }

                    // Check which fields were changed
                    Product updateProd = new Product();
                    updateProd.id = cacheProd.id;
                    bool updateNeeded = false;

                    // ... Stock
                    if (cacheProd.stock_quantity != ensoProd.stock_quantity)
                    {
                        updateNeeded = true;
                        updateProd.stock_quantity = ensoProd.stock_quantity;
                        updateProd.stock_status = ensoProd.stock_status;
                    }
                    // ... Price
                    if (cacheProd.price != ensoProd.price ||
                        cacheProd.regular_price != ensoProd.regular_price)
                    {
                        updateNeeded = true;
                        updateProd.regular_price = ensoProd.regular_price;
                        updateProd.price = ensoProd.price;
                        updateProd.sale_price = ensoProd.sale_price;
                    }

                    if (updateNeeded)
                    {
                        Log("  Product changed (id:" + cacheProd.id + ";sku:" + cacheProd.sku + ") " + cacheProd.name + " |" +
                            (updateProd.stock_quantity != null ? (" stock:" + cacheProd.stock_quantity + "->" + updateProd.stock_quantity) : ";") +
                            (updateProd.price != null ? (" price:" + cacheProd.price + "->" + updateProd.price) : ""));

                        if (productBatch.update == null) productBatch.update = new List<Product>();

                        productBatch.update.Add(updateProd);
                    }
                }
            }

            // Check if there were changes
            if (productBatch.create != null || productBatch.update != null)
            {
                Log((productBatch.create == null ? "0" : "" + productBatch.create.Count) + " new products were found");
                Log((productBatch.update == null ? "0" : "" + productBatch.update.Count) + " products changed");

                // ### COMMIT CHANGES
                Log("Commiting changes");
                BatchObject<Product> batchResult = WooHandler.BatchUpdate(productBatch);
                if (batchResult != null) Log("Upload was successful!");

                // ### UPLOAD IMAGES
                if (batchResult != null)
                {
                    if (batchResult.create != null && batchResult.create.Count > 0)
                    {
                        Log("Uploading images");
                        foreach (Product resultProduct in batchResult.create)
                        {
                            List<byte[]> productImages = DBHandler.GetImages(resultProduct.sku);

                            if (productImages != null && productImages.Count > 0)
                            {
                                Product productWithImages = WooHandler.UploadAndBindImages(resultProduct, productImages);
                                if (productWithImages != null) resultProduct.images = productWithImages.images;
                            }
                        }
                    }
                }

                // ### REFRESH CACHE
                if (batchResult != null)
                {
                    // Add new products
                    if (batchResult.create != null && batchResult.create.Count > 0)
                    {
                        Log("Adding " + batchResult.create.Count + " products to cache");
                        foreach (Product resultProd in batchResult.create)
                        {
                            if (productsCache.Find(p => p.sku == resultProd.sku) == null)
                            {
                                productsCache.Add(resultProd);
                            }
                            else { Log("CRITICAL ERROR: New product already exists in cache: " + resultProd.sku + "!"); }
                        }
                    }
                    // Overwrite changed products
                    if (batchResult.update != null && batchResult.update.Count > 0)
                    {
                        Log("Refreshing " + batchResult.update.Count + " products in cache");
                        foreach (Product resultProd in batchResult.update)
                        {
                            int cacheProdIndex = productsCache.FindIndex(p => p.id == resultProd.id);
                            if (cacheProdIndex >= 0) productsCache[cacheProdIndex] = resultProd;
                            else Log("CRITICAL ERROR: Missing product id " + resultProd.id + " in cache at updated products!");
                        }
                    }
                }
            }
            else
            {
                // Exit without doing anything
                Log("No changes were found since last update");
            }
        }

        // UI
        private void Button_ForceUpdate_Click(object sender, RoutedEventArgs e)
        {
            Log("Force updating");

            Update();
        }
        private void Button_OpenLog_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(logFile);
        }

        // Util
        string ProductToCsv(Product p)
        {
            return string.Format("{0}; {1}; {2}; {3}; {4}; {5}; {6}; {7}",
                p.id, p.sku, p.name, p.regular_price, p.price, p.sale_price, p.stock_quantity, p.stock_status);
        }
        string ProductToString(Product p)
        {
            return string.Format("(id:{0},sku:{1}) {2}; prices:({3};{4};{5}) stock:({6};{7})",
                p.id, p.sku, p.name, p.regular_price, p.price, p.sale_price, p.stock_quantity, p.stock_status);
        }
    }
}
