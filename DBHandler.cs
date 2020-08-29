using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using WooCommerceNET.WooCommerce.v3;

namespace EnsoNetSync
{
    /// <summary>
    /// This class handles all operations between the app and the MySQL database.
    /// </summary>
    class DBHandler
    {
        MySqlConnection connection;

        public event EventHandler<string> logEvent;

        public DBHandler(string connectionstring)
        {
            connection = new MySqlConnection(connectionstring);
        }

        void Log(string text, bool fileonly = false)
        {
            if (fileonly) logEvent.Invoke(this, "#" + "  " + text);
            else logEvent.Invoke(this, "  " + text);
        }

        // Get functions

        public bool TestConn()
        {
            string query = "SELECT * FROM ensoftdeb.cikk LIMIT 0;";

            try
            {
                connection.Open();
                MySqlCommand cmd = new MySqlCommand(query, connection);
                cmd.ExecuteReader();
            }
            catch (Exception E)
            {
                Log("DB connection test failed:", true);
                Log(E.ToString(), true);
                return false;
            }
            finally
            {
                connection.Close();
            }

            return true;
        }

        public List<Product> GetAllProducts()
        {
            connection.Open();

            string query = @"
                    SELECT 
	                    tkod, megnev, ar1, keszl, akcio
                    FROM 
	                    cikk c
                    WHERE
	                    netes2 = 'I'
                    ORDER BY
	                    tkod
                    ;";

            Log("Executing query");

            MySqlDataReader reader = null;
            try
            {
                MySqlCommand cmd = new MySqlCommand(query, connection);
                reader = cmd.ExecuteReader();
            }
            catch (Exception E)
            {
                Log("CRITICAL ERROR: Could not execute query! Details in log.");
                Log(E.ToString(), true);
                return null;
            }

            Log("Converting data");

            List<Product> products = new List<Product>();

            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
                    reader.IsDBNull(3) || reader.IsDBNull(4))
                {
                    Log("Converting db product " + (reader.IsDBNull(0) ? "null" : reader.GetValue(0)) + " failed");
                    continue;
                }

                Product product = new Product();
                product.sku = reader.GetString("tkod");
                product.name = reader.GetString("megnev");

                bool sale = reader.GetString("akcio") == "I";
                decimal price = reader.GetDecimal("ar1");
                int stock = reader.GetInt32("keszl");

                product.regular_price = WooHandler.PriceToRegularPrice(price, sale);
                product.price = price;
                if (sale) product.sale_price = price;

                product.stock_quantity = stock;
                product.stock_status = WooHandler.StockToStockStatus(stock);

                products.Add(product);
            }
            reader.Close();

            connection.Close();

            return products;
        }

        public List<byte[]> GetImages(string sku)
        {
            connection.Open();

            string query = string.Format(@"
                SELECT kep
                FROM ensoftdeb.cikkkepkapcs kapcs
                INNER JOIN cikkkep kep ON kapcs.kepid = kep.kepid
                WHERE tkod = '{0}'
                ;", sku);

            MySqlCommand cmd = new MySqlCommand(query, connection);

            List<byte[]> images = new List<byte[]>();

            using (MySqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    using (MemoryStream stream = new MemoryStream())
                    {
                        if (reader["kep"] != DBNull.Value)
                        {
                            byte[] image = (byte[])reader["kep"];
                            images.Add(image);
                        }
                    }
                }
            }

            connection.Close();

            return images;
        }
    }
}
