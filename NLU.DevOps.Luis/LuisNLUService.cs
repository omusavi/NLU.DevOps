﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace NLU.DevOps.Luis
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Logging;
    using Microsoft.Extensions.Logging;
    using Models;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Train, test, and cleanup a LUIS model.
    /// Implementation of <see cref="INLUService"/>
    /// </summary>
    public sealed class LuisNLUService : INLUService, IDisposable
    {
        private static readonly TimeSpan TrainStatusDelay = TimeSpan.FromSeconds(2);

        internal LuisNLUService(string appName, string appId, string appVersion, ILuisClient luisClient)
        {
            this.LuisAppName = appName ?? throw new ArgumentNullException(nameof(appName));
            this.LuisAppId = appId;
            this.LuisAppVersion = appVersion ?? "0.1.1";
            this.LuisClient = luisClient ?? throw new ArgumentNullException(nameof(luisClient));
        }

        /// <summary>
        /// Gets the name of the LUIS app.
        /// </summary>
        public string LuisAppName { get; }

        /// <summary>
        /// Gets the LUIS app ID.
        /// </summary>
        public string LuisAppId { get; private set; }

        /// <summary>
        /// Gets the LUIS app version.
        /// </summary>
        public string LuisAppVersion { get; }

        private static ILogger Logger => LazyLogger.Value;

        private static Lazy<ILogger> LazyLogger { get; } = new Lazy<ILogger>(() => ApplicationLogger.LoggerFactory.CreateLogger<LuisNLUService>());

        private ILuisClient LuisClient { get; }

        /// <inheritdoc />
        public async Task TrainAsync(
            IEnumerable<LabeledUtterance> utterances,
            IEnumerable<EntityType> entityTypes,
            CancellationToken cancellationToken)
        {
            // Validate arguments
            ValidateTrainingArguments(utterances, entityTypes);

            // Create application if not passed in.
            if (this.LuisAppId == null)
            {
                this.LuisAppId = await this.LuisClient.CreateAppAsync(this.LuisAppName, cancellationToken).ConfigureAwait(false);
                Logger.LogTrace($"Created LUIS app '{this.LuisAppName}' with ID '{this.LuisAppId}'.");
            }

            // Create LUIS import JSON
            var importJson = this.CreateImportJson(utterances, entityTypes);

            // Import the LUIS model
            Logger.LogTrace($"Importing LUIS app '{this.LuisAppName}' version '{this.LuisAppVersion}'.");
            await this.LuisClient.ImportVersionAsync(this.LuisAppId, this.LuisAppVersion, importJson, cancellationToken).ConfigureAwait(false);

            // Train the LUIS model
            Logger.LogTrace($"Training LUIS app '{this.LuisAppName}' version '{this.LuisAppVersion}'.");
            await this.LuisClient.TrainAsync(this.LuisAppId, this.LuisAppVersion, cancellationToken).ConfigureAwait(false);

            // Wait for training to complete
            await this.PollTrainingStatusAsync(cancellationToken).ConfigureAwait(false);

            // Publishes the LUIS app version
            Logger.LogTrace($"Publishing LUIS app '{this.LuisAppName}' version '{this.LuisAppVersion}'.");
            await this.LuisClient.PublishAppAsync(this.LuisAppId, this.LuisAppVersion, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<LabeledUtterance> TestAsync(
            string utterance,
            IEnumerable<EntityType> entityTypes,
            CancellationToken cancellationToken)
        {
            if (utterance == null)
            {
                throw new ArgumentNullException(nameof(utterance));
            }

            if (entityTypes == null)
            {
                throw new ArgumentNullException(nameof(entityTypes));
            }

            if (entityTypes.Any(entityType => entityType == null))
            {
                throw new ArgumentException("Entity types must not be null.", nameof(entityTypes));
            }

            if (this.LuisAppId == null)
            {
                throw new InvalidOperationException(
                    $"The '{nameof(this.LuisAppId)}' must be set before calling '{nameof(LuisNLUService.TestAsync)}'.");
            }

            var json = await this.LuisClient.QueryAsync(this.LuisAppId, utterance, cancellationToken).ConfigureAwait(false);
            return IntentJsonToLabeledUtterance(json, entityTypes);
        }

        /// <inheritdoc />
        public async Task<LabeledUtterance> TestSpeechAsync(
            string speechFile,
            IEnumerable<EntityType> entityTypes,
            CancellationToken cancellationToken)
        {
            if (speechFile == null)
            {
                throw new ArgumentNullException(nameof(speechFile));
            }

            if (entityTypes == null)
            {
                throw new ArgumentNullException(nameof(entityTypes));
            }

            if (entityTypes.Any(entityType => entityType == null))
            {
                throw new ArgumentException("Entity types must not be null.", nameof(entityTypes));
            }

            if (this.LuisAppId == null)
            {
                throw new InvalidOperationException(
                    $"The '{nameof(this.LuisAppId)}' must be set before calling '{nameof(LuisNLUService.TestSpeechAsync)}'.");
            }

            var jsonResult = await this.LuisClient.RecognizeSpeechAsync(this.LuisAppId, speechFile, cancellationToken).ConfigureAwait(false);
            return IntentJsonToLabeledUtterance(jsonResult, entityTypes);
        }

        /// <inheritdoc />
        public Task CleanupAsync(CancellationToken cancellationToken)
        {
            if (this.LuisAppId == null)
            {
                throw new InvalidOperationException(
                    $"The '{nameof(this.LuisAppId)}' must be set before calling '{nameof(LuisNLUService.CleanupAsync)}'.");
            }

            return this.LuisClient.DeleteAppAsync(this.LuisAppId, cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.LuisClient.Dispose();
        }

        private static void ValidateTrainingArguments(IEnumerable<LabeledUtterance> utterances, IEnumerable<EntityType> entityTypes)
        {
            if (utterances == null)
            {
                throw new ArgumentNullException(nameof(utterances));
            }

            if (entityTypes == null)
            {
                throw new ArgumentNullException(nameof(entityTypes));
            }

            if (utterances.Any(utterance => utterance == null))
            {
                throw new ArgumentException("Utterances must not be null.", nameof(utterances));
            }

            if (entityTypes.Any(entityType => entityType == null))
            {
                throw new ArgumentException("Entity types must not be null.", nameof(entityTypes));
            }
        }

        private static LabeledUtterance IntentJsonToLabeledUtterance(JObject intentJson, IEnumerable<EntityType> entityTypes)
        {
            if (intentJson == null)
            {
                return new LabeledUtterance(null, null, null);
            }

            var renamedEntityTypes = entityTypes
                .Where(entityType => entityType.Kind == "builtin")
                .ToDictionary(entityType => $"builtin.{entityType.Data.Value<string>("name")}", entityType => entityType.Name);

            var text = intentJson.Value<string>("query");
            var intent = intentJson.SelectToken(".topScoringIntent.intent").Value<string>();

            var array = (JArray)intentJson["entities"];
            var entities = new List<Entity>(array.Count);
            foreach (var item in array)
            {
                var entityType = item.Value<string>("type");
                if (renamedEntityTypes.TryGetValue(entityType, out var renamedEntityType))
                {
                    entityType = renamedEntityType;
                }

                var entityValue = GetEntityValue(item);
                var startCharIndex = item.Value<int>("startIndex");
                var endCharIndex = item.Value<int>("endIndex");

                var matchText = item.Value<string>("entity");
                var matches = Regex.Matches(text, matchText, RegexOptions.IgnoreCase);
                var matchIndex = -1;
                for (var i = 0; i < matches.Count; ++i)
                {
                    if (matches[i].Index == startCharIndex)
                    {
                        matchIndex = i;
                        break;
                    }
                }

                Debug.Assert(matchIndex >= 0, "Invalid LUIS response.");
                entities.Add(new Entity(entityType, entityValue, matchText, matchIndex));
            }

            return new LabeledUtterance(text, intent, entities);
        }

        private static string GetEntityValue(JToken entityJson)
        {
            var resolution = entityJson["resolution"];
            if (resolution == null)
            {
                return null;
            }

            var value = resolution.Value<string>("value");
            if (value != null)
            {
                return value;
            }

            // TODO: choose "the best" entity value from resolution.
            var resolvedValue = resolution["values"][0];
            return resolvedValue is JObject resolvedObject
                ? resolvedObject.Value<string>("value")
                : resolvedValue.Value<string>();
        }

        private JObject CreateImportJson(IEnumerable<LabeledUtterance> utterances, IEnumerable<EntityType> entityTypes)
        {
            // Get boilerplate JObject
            var importJson = this.GetModelStarter();

            // Add intents to model
            var intents = utterances
                .Select(utterance => utterance.Intent)
                .Append("None")
                .Distinct()
                .Select(intent => new JObject { { "name", intent } });
            var intentsArray = (JArray)importJson["intents"];
            intentsArray.AddRange(intents);

            // Add utterances to model
            var luisUtterances = utterances
                .Select(item => JObject.FromObject(LuisLabeledUtterance.FromLabeledUtterance(item, entityTypes)));
            var utteranceArray = (JArray)importJson["utterances"];
            utteranceArray.AddRange(luisUtterances);

            // Add entities to model
            var entitiesArray = (JArray)importJson["entities"];
            var modelFeatures = (JArray)importJson["model_features"];
            var prebuiltEntitiesArray = (JArray)importJson["prebuiltEntities"];
            var closedListsArray = (JArray)importJson["closedLists"];
            foreach (var entityType in entityTypes)
            {
                switch (entityType.Kind)
                {
                    case "simple":
                        var simpleEntity = new JObject
                        {
                            { "name", entityType.Name },
                        };

                        simpleEntity.Merge(entityType.Data);
                        entitiesArray.Add(simpleEntity);
                        break;
                    case "builtin":
                        prebuiltEntitiesArray.Add(entityType.Data);
                        break;
                    case "list":
                        var listEntity = new JObject
                        {
                            { "name", entityType.Name }
                        };

                        listEntity.Merge(entityType.Data);
                        closedListsArray.Add(listEntity);
                        break;
                    case "phrases":
                        var phraseEntity = new JObject
                        {
                            { "name", entityType.Name }
                        };

                        phraseEntity.Merge(entityType.Data);
                        modelFeatures.Add(phraseEntity);
                        break;
                    default:
                        throw new NotImplementedException($"Entity type '{entityType.Kind}' has not been implemented.");
                }
            }

            return importJson;
        }

        private JObject GetModelStarter()
        {
            return new JObject
            {
                { "luis_schema_version", "3.0.0" },
                { "versionId", this.LuisAppVersion },
                { "name", this.LuisAppName },
                { "desc", string.Empty },
                { "culture", "en-us" },
                { "intents", new JArray() },
                { "entities", new JArray() },
                { "composites", new JArray() },
                { "closedLists", new JArray() },
                { "patternAnyEntities", new JArray() },
                { "regex_entities", new JArray() },
                { "prebuiltEntities", new JArray() },
                { "model_features", new JArray() },
                { "regex_features", new JArray() },
                { "patterns", new JArray() },
                { "utterances", new JArray() },
            };
        }

        private async Task PollTrainingStatusAsync(CancellationToken cancellationToken)
        {
            JArray trainStatusJson;
            while (true)
            {
                trainStatusJson = await this.LuisClient.GetTrainingStatusAsync(this.LuisAppId, this.LuisAppVersion, cancellationToken).ConfigureAwait(false);
                var inProgress = trainStatusJson.SelectTokens("[*].details.status")
                    .Select(statusJson => statusJson.Value<string>())
                    .Any(status => status == "InProgress" || status == "Queued");

                if (!inProgress)
                {
                    break;
                }

                Logger.LogTrace($"Training jobs not complete. Polling again.");
                await Task.Delay(TrainStatusDelay, cancellationToken).ConfigureAwait(false);
            }

            // Ensure no failures occurred while training
            var failures = trainStatusJson.SelectTokens("[?(@.details.status == 'Fail')]");
            if (failures.Any())
            {
                throw new InvalidOperationException("Failure occurred while training LUIS model.");
            }
        }
    }
}