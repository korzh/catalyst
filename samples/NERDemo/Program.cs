using System;
using System.Threading.Tasks;
using System.IO;

using Catalyst;
using Catalyst.Models;
using Mosaik.Core;

namespace CatalystTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var dataFilePath = Path.Combine("data", "eq-intro.txt");

            var docText = File.ReadAllText(dataFilePath);
            Storage.Current = new OnlineRepositoryStorage(new DiskStorage("catalyst-models"));
            var nlp = await Pipeline.ForAsync(Language.English);

            var ner = await AveragePerceptronEntityRecognizer.FromStoreAsync(Language.English, -1, "WikiNER");
            nlp.Add(ner);
            //nlp.Add(new AveragePerceptronEntityRecognizer(Language.English, 0, "WikiNER", new string[] { "Person", "Organization", "Location" }));
 
            var doc = new Document(docText, Language.English);
            nlp.ProcessSingle(doc);
            Console.WriteLine("Sentences: " + doc.TokensData.Count);
            Console.WriteLine("Entities: " + doc.EntitiesCount);
            Console.WriteLine(doc.ToJson());
        }

        static async Task ListAllPublicModels()
        {
            foreach (var lang in Enum.GetValues(typeof(Language))) {
                try {
                    var nlp = await Pipeline.ForAsync((Language)lang);
                    Console.WriteLine("OK: " + Enum.GetName(typeof(Language), lang));
                }
                catch {
                    Console.WriteLine("NOT FOUND: " + Enum.GetName(typeof(Language), lang));
                }
            }
        }
    }
}
