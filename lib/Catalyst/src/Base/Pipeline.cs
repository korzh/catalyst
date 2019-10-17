﻿using Microsoft.Extensions.Logging;
using Mosaik.Core;
using Catalyst.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Catalyst
{
    [FormerName("Mosaik.NLU", "PipelineModelData")]
    public class PipelineData : StorableObjectData
    {
        public List<StoredObjectInfo> Processes { get; set; }
    }

    public class Pipeline : StorableObject<Pipeline, PipelineData>, ICanUpdateModel
    {
        private List<IProcess> Processes { get; set; } = new List<IProcess>();
        private Dictionary<Language, Neuralyzer> Neuralyzers { get; set; } = new Dictionary<Language, Neuralyzer>();

        private ReaderWriterLockSlim RWLock = new ReaderWriterLockSlim();

        public Pipeline(Language language = Language.Any, int version = 0, string tag = "") : base(language, version, tag)
        {
        }

        public Pipeline(IList<IProcess> processes, Language language = Language.Any, int version = 0, string tag = "") : this(language, version, tag)
        {
            Processes = processes.ToList();
        }

        public new static async Task<Pipeline> FromStoreAsync(Language language, int version, string tag)
        {
            var a = new Pipeline(language, version, tag);
            await a.LoadDataAsync();
            var set = new HashSet<string>();
            foreach (var md in a.Data.Processes.ToArray()) //Copy here as we'll modify the list bellow if model not found
            {
                if (await md.ExistsAsync())
                {
                    try
                    {
                        if (set.Add(md.ToStringWithoutVersion()))
                        {
                            var process = (IProcess)await md.FromStoreAsync();
                            a.Add(process);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        Logger.LogError($"Model not found on disk, ignoring: {md.ToString()}");
                        a.Data.Processes.Remove(md);
                    }
                }
                else
                {
                    a.Data.Processes.Remove(md);
                }
            }

            if (!a.Processes.Any(p => p is ITokenizer))
            {
                a.AddToBegin(new FastTokenizer(language)); //Fix bug that removed all tokenizers from the pipelines due to missing ExistsAsync
            }

            return a;
        }

        public static async Task<bool> CheckIfHasModelAsync(Language language, int version, string tag, StoredObjectInfo model, bool matchVersion = true)
        {
            var a = new Pipeline(language, version, tag);
            await a.LoadDataAsync();
            return a.Data.Processes.Any(p => p.Language == model.Language && p.ModelType == model.ModelType && p.Tag == model.Tag && (!matchVersion || p.Version == model.Version));
        }

        public override async Task StoreAsync()
        {
            Data.Processes = new List<StoredObjectInfo>();
            var set = new HashSet<string>();
            foreach (var p in Processes)
            {
                var md = new StoredObjectInfo(p.Type, p.Language, p.Version, p.Tag);
                if (set.Add(md.ToStringWithoutVersion()))
                {
                    Data.Processes.Add(md);
                }
            }
            await base.StoreAsync();
        }

        public void AddSpecialCase(string word, TokenizationException exception)
        {
            foreach (var p in Processes)
            {
                if (p is FastTokenizer st)
                {
                    st.AddSpecialCase(word, exception);
                }
            }
        }

        public Pipeline UseNeuralyzer(Neuralyzer neuralyzer)
        {
            RWLock.EnterWriteLock();
            try
            {
                Neuralyzers[neuralyzer.Language] = neuralyzer;
                return this;
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
        }

        public Pipeline UseNeuralyzers(IEnumerable<Neuralyzer> neuralyzeres)
        {
            RWLock.EnterWriteLock();
            try
            {
                Neuralyzers.Clear();
                foreach (var n in neuralyzeres)
                {
                    Neuralyzers[n.Language] = n;
                }
                return this;
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
        }

        public void RemoveAllNeuralizers()
        {
            RWLock.EnterWriteLock();
            try
            {
                Neuralyzers.Clear();
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
        }

        private void TryImportSpecialCases(IProcess process)
        {
            if (process is IHasSpecialCases)
            {
                foreach (FastTokenizer st in Processes.Where(p => p is FastTokenizer))
                {
                    if (process.Language == Language.Any || st.Language == process.Language)
                    {
                        st.ImportSpecialCases(process);
                    }
                }
            }
        }

        public Pipeline AddToBegin(IProcess process)
        {
            RWLock.EnterWriteLock();
            try
            {
                var tmp = Processes.ToList();
                Processes.Clear();
                Processes.Add(process);
                Processes.AddRange(tmp);
                TryImportSpecialCases(process);
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
            return this;
        }

        public void RemoveAll(Predicate<IProcess> p)
        {
            RWLock.EnterWriteLock();

            try
            {
                Processes.RemoveAll(p);
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
        }

        public Pipeline Add(IProcess process)
        {
            RWLock.EnterWriteLock();
            try
            {
                Processes.Add(process);
                TryImportSpecialCases(process);
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
            return this;
        }

        public void ReplaceWith(Pipeline newPipeline)
        {
            RWLock.EnterWriteLock();
            try
            {
                Data.Processes = newPipeline.Data.Processes;
                Processes = newPipeline.Processes.ToList();
                Version = newPipeline.Version;
            }
            finally
            {
                RWLock.ExitWriteLock();
            }
        }

        public static Pipeline For(Language language, bool sentenceDetector = true, bool tagger = true)
        {
            var p = new Pipeline(language);
            p.Add(new FastTokenizer(language));
            if (sentenceDetector) { p.Add(SentenceDetector.FromStoreAsync(language, -1, "").WaitResult()); }
            if (tagger) { p.Add(AveragePerceptronTagger.FromStoreAsync(language, -1, "").WaitResult()); }
            return p;
        }

        public static async Task<Pipeline> ForAsync(Language language, bool sentenceDetector = true, bool tagger = true)
        {
            var p = new Pipeline(language);
            p.Add(new FastTokenizer(language));
            if (sentenceDetector) { p.Add(await SentenceDetector.FromStoreAsync(language, 0, "")); }
            if (tagger) { p.Add(await AveragePerceptronTagger.FromStoreAsync(language, 0, "")); }
            return p;
        }

        public static async Task<Pipeline> For(IEnumerable<Language> languages, bool sentenceDetector = true, bool tagger = true)
        {
            var processes = new List<IProcess>();
            foreach (var language in languages)
            {
                processes.Add(new FastTokenizer(language));
                if (sentenceDetector) { processes.Add(await SentenceDetector.FromStoreAsync(language, -1, "")); }
                if (tagger) { processes.Add(await AveragePerceptronTagger.FromStoreAsync(language, -1, "")); }
            }
            var p = new Pipeline(processes) { Language = Language.Any };
            return p;
        }

        public static async Task<Pipeline> TokenizerFor(Language language)
        {
            var p = new Pipeline() { Language = language };
            p.Add(new FastTokenizer(language));

            IProcess sd = null;

            try
            {
                //Uses english sentence detector as a default
                sd = await SentenceDetector.FromStoreAsync((language == Language.Any) ? Language.English : language, -1, "");
                p.Add(sd);
            }
            catch
            {
                Logger.LogWarning("Could not find sentence detector model for language {LANGUAGE}. Falling back to english model", language);
            }

            if (sd is null)
            {
                try
                {
                    sd = await SentenceDetector.FromStoreAsync(Language.English, -1, "");
                    p.Add(sd);
                }
                catch
                {
                    Logger.LogWarning("Could not find sentence detector model for language {LANGUAGE}. Continuing without one", Language.English);
                }
            }

            return p;
        }

        //TODO: FIX THIS TO HAVE SAME FALLBACK TO ENGLISH FOR SENTENCE DETECTOR
        //public static async Task<Pipeline> TokenizerFor(IEnumerable<Language> languages)
        //{
        //    var processes = new List<IProcess>();
        //    foreach (var language in languages)
        //    {
        //        processes.Add(new SimpleTokenizer(language));
        //        processes.Add(await SentenceDetector.FromStoreAsync(language, 0, "").WaitResult());
        //    }
        //    var p = new Pipeline(processes) { Language = Language.Any};
        //    return p;
        //}

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDocument ProcessSingle(IDocument document)
        {
            RWLock.EnterReadLock();
            try
            {
                return ProcessSingleWithoutLocking(document);
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IDocument ProcessSingleWithoutLocking(IDocument document)
        {
            if (document.Length > 0)
            {
                foreach (var p in Processes)
                {
                    if (p.Language != Language.Any && document.Language != Language.Any && p.Language != document.Language) { continue; }
                    p.Process(document);
                }

                //Apply any neuralizer registered for any language, or the document language
                if (Neuralyzers.TryGetValue(Language.Any, out var neuralyzerAny)) { neuralyzerAny.Process(document); }
                if (Neuralyzers.TryGetValue(document.Language, out var neuralyzerLang)) { neuralyzerLang.Process(document); }
            }
            return document;
        }

        public IEnumerable<IDocument> ProcessSingleThread(IEnumerable<IDocument> documents)
        {
            RWLock.EnterReadLock();

            var sw = Stopwatch.StartNew();
            long docsCount = 0, spansCount = 0, tokensCount = 0;

            Logger.LogTrace("Started pipeline single thread processing");

            var buffer = new List<IDocument>();

            foreach (var block in documents.Split(1000))
            {
                RWLock.EnterReadLock(); //Acquire the read lock only for the duration of the block processing, not during the yield return
                try
                {
                    foreach (var doc in block)
                    {
                        IDocument d;
                        try
                        {
                            d = ProcessSingleWithoutLocking(doc);

                            Interlocked.Add(ref spansCount, doc.SpansCount);
                            Interlocked.Add(ref tokensCount, doc.TokensCount);

                            if (Interlocked.Increment(ref docsCount) % 10_000 == 0)
                            {
                                var elapsed = sw.Elapsed.TotalSeconds;
                                var kts = tokensCount / elapsed / 1000;
                                Logger.LogInformation("Parsed {DOCS} documents, {SPANS} sentences and {TOKENS} tokens in {ELAPSED:0.00} seconds at {KTS} kTokens/second", docsCount, spansCount, tokensCount, (int)elapsed, (int)kts);
                            }
                        }
                        catch (Exception E)
                        {
                            Logger.LogError(E, "Error parsing document");
                            d = null;
                        }

                        if (d is object)
                        {
                            buffer.Add(d);
                        }
                    }
                }
                finally
                {
                    RWLock.ExitReadLock(); //Return the lock here, otherwise we don't have control due to the yield return pattern
                }

                foreach(var d in block)
                {
                    yield return d;
                }
            }
        }

        public IEnumerable<IDocument> Process(IEnumerable<IDocument> documents, ParallelOptions parallelOptions = default)
        {
            var sw = Stopwatch.StartNew();
            long docsCount = 0, spansCount = 0, tokensCount = 0;
            double kts;

            var enumerator = documents.GetEnumerator();
            var buffer = new List<IDocument>();

            parallelOptions = parallelOptions ?? new ParallelOptions();

            using (var m = new Measure(Logger, "Parsing documents", logOnlyDuration:true))
            {
                while (enumerator.MoveNext())
                {
                    buffer.Add(enumerator.Current);

                    if (buffer.Count >= 10_000)
                    {

                        RWLock.EnterReadLock(); //Acquire the read lock only for the duration of the processing, not during the yield return
                        try
                        {
                            Parallel.ForEach(buffer, parallelOptions, (doc) => ProcessSingleWithoutLocking(doc));
                        }
                        finally
                        {
                            RWLock.ExitReadLock(); //Return the lock here, otherwise we don't have control due to the yield return pattern
                        }

                        foreach (var doc in buffer) { spansCount += doc.SpansCount; tokensCount += doc.TokensCount; docsCount++; yield return doc; }
                        buffer.Clear();
                        kts = (double)tokensCount / m.ElapsedSeconds / 1000;
                        m.SetOperations(docsCount).EmitPartial($"{kts:n0} kTokens/second, found {spansCount:n0} sentences and {tokensCount:n0} tokens");
                    }
                }

                RWLock.EnterReadLock(); //Acquire the read lock only for the duration of the processing, not during the yield return
                try
                {
                    //Process any remaining
                    Parallel.ForEach(buffer, parallelOptions, (doc) => ProcessSingleWithoutLocking(doc));
                }
                finally
                {
                    RWLock.ExitReadLock(); //Return the lock here, otherwise we don't have control due to the yield return pattern
                }

                foreach (var doc in buffer) { spansCount += doc.SpansCount; tokensCount += doc.TokensCount; docsCount++; yield return doc; }
                buffer.Clear();
                kts = (double)tokensCount / m.ElapsedSeconds / 1000;
                m.SetOperations(docsCount).EmitPartial($"{kts:n0} kTokens/second, found {spansCount:n0} sentences and {tokensCount:n0} tokens");
            }
        }

        public IList<IModel> GetModelsList()
        {
            RWLock.EnterReadLock();
            try
            {
                return Processes.Select(p => (IModel)p).ToList();
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public List<StoredObjectInfo> GetModelsDescriptions()
        {
            RWLock.EnterReadLock();
            try
            {
                return Processes.Select(p => new StoredObjectInfo(p.Type, p.Language, p.Version, p.Tag)).ToList();
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public IEnumerable<(StoredObjectInfo Model, string[] EntityTypes)> GetPossibleEntityTypes()
        {
            RWLock.EnterReadLock();
            try
            {
                foreach (var p in Processes)
                {
                    if (p is IEntityRecognizer ire)
                    {
                        var md = new StoredObjectInfo(ire);
                        yield return (md, ire.Produces());
                    }
                }
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public bool HasModel(StoredObjectInfo model, bool matchVersion = true)
        {
            RWLock.EnterReadLock();

            try
            {
                return Processes.Any(p => p.Language == model.Language && p.Type == model.ModelType && p.
                Tag == model.Tag && (!matchVersion || p.Version == model.Version));
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public bool RemoveModel(StoredObjectInfo model)
        {
            //Removes any model matching the description, independent of the version
            RWLock.EnterReadLock();

            try
            {
                return Processes.RemoveAll(p => p.Language == model.Language && p.Type == model.ModelType && p.Tag == model.Tag) > 0;
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public bool HasModelToUpdate(StoredObjectInfo newModel)
        {
            RWLock.EnterReadLock();

            try
            {
                return Processes.Any(p => p.Language == newModel.Language && p.Type == newModel.ModelType && p.Tag == newModel.Tag && p.Version < newModel.Version);
            }
            finally
            {
                RWLock.ExitReadLock();
            }
        }

        public void UpdateModel(StoredObjectInfo newModel, object modelToBeUpdated)
        {
            if (modelToBeUpdated is IProcess process)
            {
                RWLock.EnterWriteLock();

                try
                {
                    var toBeReplaced = Processes.FirstOrDefault(p => p.Language == newModel.Language && p.Type == newModel.ModelType && p.Tag == newModel.Tag && p.Version < newModel.Version);

                    if (toBeReplaced is object)
                    {
                        var index = Processes.IndexOf(toBeReplaced);
                        if (index >= 0)
                        {
                            Processes[index] = process;
                        }
                    }
                }
                finally
                {
                    RWLock.ExitWriteLock();
                }
            }
            else
            {
                throw new InvalidOperationException("Invalid model to update");
            }
        }
    }
}