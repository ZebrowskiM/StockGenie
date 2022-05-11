using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StockGenie.DataObjects
{
    [Serializable]
    public  class CryptoListitem
    {
        public string id { get; set; }
        public string name { get; set; }
        public string ticker { get; set; }
    }
}
