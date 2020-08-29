using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WooCommerceNET;
using WooCommerceNET.Base;
using WooCommerceNET.WooCommerce.v3;

namespace EnsoNetSync
{
    /// <summary>
    /// This class handles the operations towards the Webshop through the WooCommerceNET library and the Wordpress Media Rest API.
    /// </summary>
    class WooHandler
    {
        WCObject woocommerce;
        RestClient wordpressMedia;

        readonly string restAuthToken;
        readonly string restCookie;

        public event EventHandler<string> logEvent;

        public WooHandler(string url, string restApiClientKey, string restApiClientSecret, 
            string restauth, string cookie)
        {
            RestAPI restApi = new RestAPI(url + "/wp-json/wc/v3/", restApiClientKey, restApiClientSecret);
            woocommerce = new WCObject(restApi);

            this.restAuthToken = restauth;
            this.restCookie = cookie;

            wordpressMedia = new RestClient(url + "/wp-json/wp/v2/media");
            wordpressMedia.Timeout = 6000;
        }

        void Log(string text, bool fileonly = false)
        {
            if (fileonly) logEvent.Invoke(this, "#" + "  " + text);
            else logEvent.Invoke(this, "  " + text);
        }

        // Functions

        public List<Product> GetAllProducts()
        {
            List<Product> allProducts = new List<Product>();
            List<Product> tempProducts = new List<Product>();
            int page = 1;

            while ((tempProducts = woocommerce.Product.GetAll(
                    new Dictionary<string, string>() {
                        { "page", page.ToString() },
                        { "per_page", "100" }
                    }).Result).Count > 0)
            {
                Log("Got " + tempProducts.Count + " products from page " + page);
                allProducts.AddRange(tempProducts);
                page++;
            }

            return allProducts;
        }

        Dictionary<string, object> _uploadImage(string sku, byte[] imagedata)
        {
            Log($"Uploading image {imagedata} for product {sku}", true);

            if (sku == null || imagedata == null)
            {
                Log("Missing info!", true);
                return null;
            }

            RestRequest request = new RestRequest(Method.POST);
            request.AddHeader("Content-Disposition", "attachment; filename=\"" + sku + ".jpg\"");
            request.AddHeader("Authorization", restAuthToken);
            request.AddHeader("Content-Type", "image/jpeg");
            request.AddHeader("Cookie", restCookie);
            request.AddParameter("image/jpeg", imagedata, ParameterType.RequestBody);

            IRestResponse response = null;
            try
            {
                response = wordpressMedia.Execute(request);
            }
            catch (Exception E)
            {
                Log("Error while sending request: " + E.ToString(), true);
                return null;
            }

            if (response == null || !response.IsSuccessful)
            {
                Log("Upload failed: " + (response == null ? "response was null" : response.Content), true);
                return null;
            }

            Dictionary<string, object> resultDict = null;
            try
            {
                resultDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(response.Content);

                long id = (long)resultDict["id"];
                string src = (string)resultDict["source_url"];
            }
            catch (Exception E)
            {
                Log("Deserialization failed: " + E.ToString(), true);
                return null;
            }

            return resultDict;
        }

        Dictionary<string, object> _bindImage(string imagePostId, string prodPostId)
        {
            Log($"Binding image {imagePostId} to product {prodPostId}", true);

            if (imagePostId == null || prodPostId == null)
            {
                Log("Missing info!", true);
                return null;
            }

            RestRequest request = new RestRequest(imagePostId, Method.POST);
            request.AddHeader("Authorization", restAuthToken);
            request.AddHeader("Cookie", restCookie);
            request.AddParameter("post", prodPostId);

            IRestResponse response = null;
            try
            {
                response = wordpressMedia.Execute(request);
            }
            catch (Exception E)
            {
                Log("Error while sending request: " + E.ToString(), true);
                return null;
            }

            if (response == null || !response.IsSuccessful)
            {
                Log("Binding failed: " + (response == null ? "response was null" : response.Content), true);
                return null;
            }

            Dictionary<string, object> resultDict = null;
            try
            {
                resultDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(response.Content);

                long id = (long)resultDict["id"];
                string src = (string)resultDict["source_url"];
            }
            catch (Exception E)
            {
                Log("Deserialization failed: " + E.ToString(), true);
                return null;
            }

            return resultDict;
        }

        /// <summary>
        /// Uploads image and create a two-way link between it and the product.
        /// </summary>
        /// <returns>Returns the product with images list, null if upload failed.</returns>
        public Product UploadAndBindImages(Product product, List<byte[]> images)
        {
            if (product == null || product.id == null || images == null || images.Count == 0)
            {
                Log("Unable to upload: Missing information!", true);
                Log($"product={product}; id={product?.id}; images=({images?.Count}) {images}", true);
            }
            else
            {
                List<ProductImage> productImages = new List<ProductImage>();

                // Iterate images
                foreach (byte[] image in images)
                {
                    // Try upload the image
                    Dictionary<string, object> imageResultDict =
                        _uploadImage(product.sku, ReformatImage(image));

                    if (imageResultDict != null)
                    {
                        // Image uploaded, now bind to product
                        Dictionary<string, object> bindResultDict =
                            _bindImage(imageResultDict["id"]?.ToString() ?? null, product.id.ToString());

                        if (bindResultDict == null) Log("ERROR: Binding failed but image uploaded!");

                        // Create ProductImage for updating later
                        ProductImage productImage = new ProductImage();
                        productImage.id = (long)imageResultDict["id"];
                        productImage.src = (string)imageResultDict["source_url"];

                        productImages.Add(productImage);
                    }
                }

                if (productImages.Count > 0)
                {
                    // Create updateProduct
                    Product productWithImages = new Product();
                    productWithImages.id = product.id;
                    productWithImages.images = productImages;

                    // Update
                    Product resultProduct = woocommerce.Product.Update(product.id ?? -1, productWithImages).Result;
                    if (resultProduct != null && resultProduct.images != null && resultProduct.images.Count > 0)
                    {
                        // Pretty print uploaded images id
                        string imagesPretty = "";
                        for (int i = 0; i < productImages.Count; i++)
                        { imagesPretty += productImages[i].id + (i == productImages.Count - 1 ? "" : ", "); }

                        Log($"Image(s) {imagesPretty} uploaded for product {product.sku}");
                        return resultProduct;
                    }
                    else
                    {
                        Log("ERROR: Result product or images missing!");
                        Log($"resultProduct={resultProduct}; images=({resultProduct?.images?.Count}) {resultProduct?.images}", true);
                    }
                }
            }

            Log($"Image upload failed for product {product?.sku}!");
            return null;
        }

        /// <summary>
        /// Create or Update any size of product batch.
        /// </summary>
        public BatchObject<Product> BatchUpdate(ProductBatch productBatch)
        {
            if ((productBatch.create == null || productBatch.create.Count <= 100) &&
                (productBatch.update == null || productBatch.update.Count <= 100))
            {
                // Product batch is optimal size, can upload
                Log("Uploading batch");
                try
                {
                    return woocommerce.Product.UpdateRange(productBatch).Result;
                }
                catch(Exception E)
                {
                    Log("CRITICAL ERROR: Uploading batch failed! Details in log.");
                    Log(E.ToString(), true);
                    return null;
                }
            }
            else
            {
                // Every result goes into one batchobject
                BatchObject<Product> batchResult = new BatchObject<Product>();
                batchResult.create = new List<Product>();
                batchResult.update = new List<Product>();

                // Split into Create and Update batch and upload separately in chunks
                // ... Create
                ProductBatch createBatch = null;
                if (productBatch.create != null && productBatch.create.Count > 0)
                {
                    createBatch = new ProductBatch();
                    createBatch.create = new List<Product>();
                    createBatch.create.AddRange(productBatch.create);
                }
                productBatch.create = null;

                if (createBatch != null)
                {
                    Log("Uploading create batch");
                    if (createBatch.create.Count <= 100)
                    {
                        try
                        {
                            BatchObject<Product> smallResult =
                            woocommerce.Product.UpdateRange(createBatch).Result;

                            batchResult.create.AddRange(smallResult.create);
                        }
                        catch (Exception E)
                        {
                            Log("CRITICAL ERROR: Uploading batch failed! Details in log.");
                            Log(E.ToString(), true);
                            return null;
                        }
                    }
                    else
                    {
                        Log("Batch is too large, splitting");

                        List<Product> productsCopy = new List<Product>();
                        productsCopy.AddRange(createBatch.create);

                        List<List<Product>> productsChunks = new List<List<Product>>();

                        while (productsCopy.Any())
                        {
                            productsChunks.Add(productsCopy.Take(100).ToList());
                            productsCopy = productsCopy.Skip(100).ToList();
                        }

                        foreach (List<Product> productsChunk in productsChunks)
                        {
                            Log("Uploading create batch of " + productsChunk.Count + " products");

                            ProductBatch smallBatch = new ProductBatch();
                            smallBatch.create = productsChunk;

                            try
                            {
                                BatchObject<Product> smallResult =
                                woocommerce.Product.UpdateRange(smallBatch).Result;

                                batchResult.create.AddRange(smallResult.create);
                            }
                            catch (Exception E)
                            {
                                Log("CRITICAL ERROR: Uploading batch failed! Details in log.");
                                Log(E.ToString(), true);
                                return null;
                            }
                        }
                    }
                }

                // ... Update
                ProductBatch updateBatch = null;
                if (productBatch.update != null && productBatch.update.Count > 0)
                {
                    updateBatch = new ProductBatch();
                    updateBatch.update = new List<Product>();
                    updateBatch.update.AddRange(productBatch.update);
                }
                productBatch.update = null;

                if (updateBatch != null)
                {
                    Log("Uploading update batch");
                    if (updateBatch.update.Count <= 100)
                    {
                        try
                        {
                            BatchObject<Product> smallResult =
                            woocommerce.Product.UpdateRange(updateBatch).Result;

                            batchResult.update.AddRange(smallResult.update);
                        }
                        catch (Exception E)
                        {
                            Log("CRITICAL ERROR: Uploading batch failed! Details in log.");
                            Log(E.ToString(), true);
                            return null;
                        }
                    }
                    else
                    {
                        Log("Batch is too large, splitting");

                        List<Product> productsCopy = new List<Product>();
                        productsCopy.AddRange(updateBatch.update);

                        List<List<Product>> productsChunks = new List<List<Product>>();

                        while (productsCopy.Any())
                        {
                            productsChunks.Add(productsCopy.Take(100).ToList());
                            productsCopy = productsCopy.Skip(100).ToList();
                        }

                        foreach (List<Product> productsChunk in productsChunks)
                        {
                            Log("Uploading update batch of " + productsChunk.Count + " products");

                            ProductBatch smallBatch = new ProductBatch();
                            smallBatch.update = productsChunk;

                            try
                            {
                                BatchObject<Product> smallResult =
                                woocommerce.Product.UpdateRange(smallBatch).Result;

                                batchResult.update.AddRange(smallResult.update);
                            }
                            catch (Exception E)
                            {
                                Log("CRITICAL ERROR: Uploading batch failed! Details in log.");
                                Log(E.ToString(), true);
                                return null;
                            }
                        }
                    }
                }

                return batchResult;
            }
        }

        /// <summary>
        /// Converts an image in byteArray to Bitmap, then back to byteArray to make the upload possible.
        /// </summary>
        byte[] ReformatImage(byte[] byteArray)
        {
            if (byteArray == null) return null;

            ImageConverter imageConverter = new ImageConverter();

            Bitmap bm = (Bitmap)imageConverter.ConvertFrom(byteArray);

            if (bm != null && (bm.HorizontalResolution != (int)bm.HorizontalResolution || bm.VerticalResolution != (int)bm.VerticalResolution))
            { bm.SetResolution((int)(bm.HorizontalResolution + 0.5f), (int)(bm.VerticalResolution + 0.5f)); }

            return (byte[])imageConverter.ConvertTo(bm, typeof(byte[]));
        }

        public bool TestConnWP()
        {
            var request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", restAuthToken);
            request.AddHeader("Cookie", restCookie);

            IRestResponse response = null;
            try
            {
                response = wordpressMedia.Execute(request);
            }
            catch (Exception E)
            {
                Log("Wordpress connection test failed:", true);
                Log(E.ToString(), true);
                return false;
            }

            if (response == null) return false;

            if (response.IsSuccessful) return true;
            else
            {
                Log("Wordpress connection test failed:", true);
                Log(response.Content, true);
                return false;
            }
        }
        public bool TestConnWoo()
        {
            try
            {
                List<Setting> settings = woocommerce.Setting.GetAll().Result;
                if (settings != null && settings.Count > 0) return true;
                else return false;
            }
            catch (Exception E)
            {
                Log("Woocommerce connection test failed:", true);
                Log(E.ToString(), true);
                return false;
            }
        }

        public static string StockToStockStatus(int stock)
        {
            return stock > 0 ? "instock" : "outofstock";
        }
        public static decimal PriceToRegularPrice(decimal price, bool sale)
        {
            return sale ? Math.Round(price * (decimal)1.2, 4) : price;
        }
    }
}
