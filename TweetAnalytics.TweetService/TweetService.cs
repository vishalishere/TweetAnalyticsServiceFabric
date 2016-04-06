﻿namespace TweetAnalytics.TweetService
{
    #region

    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using LinqToTwitter;

    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Remoting.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;

    using Newtonsoft.Json;

    using TweetAnalytics.Contracts;

    #endregion

    public class TweetService : StatefulService, ITweet
    {
        #region Fields

        private CancellationToken cancellationToken;

        #endregion

        #region Public Methods and Operators

        public async Task<TweetScore> GetAverageSentimentScore()
        {
            if (this.cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var tweetScore = new TweetScore();
            var scoreDictionary =
                await this.StateManager.GetOrAddAsync<IReliableDictionary<string, decimal>>("scoreDictionary");
            using (var tx = this.StateManager.CreateTransaction())
            {
                tweetScore.TweetCount = await scoreDictionary.GetCountAsync(tx);
                tweetScore.TweetSentimentAverageScore = tweetScore.TweetCount == 0 ? 0 :
                    scoreDictionary.CreateEnumerableAsync(tx).Result.Average(x => x.Value);
            }

            return tweetScore;
        }

        public async Task SetTweetSubject(string subject)
        {
            if (this.cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(subject))
            {
                return;
            }

            using (var tx = this.StateManager.CreateTransaction())
            {
                var scoreDictionary =
                    await this.StateManager.GetOrAddAsync<IReliableDictionary<string, decimal>>("scoreDictionary");
                await scoreDictionary.ClearAsync();
                var topicQueue = await this.StateManager.GetOrAddAsync<IReliableQueue<string>>("topicQueue");
                while (topicQueue.TryDequeueAsync(tx).Result.HasValue)
                {
                }
                await topicQueue.EnqueueAsync(tx, subject);
                await tx.CommitAsync();
            }
        }

        #endregion

        #region Methods

        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new List<ServiceReplicaListener>
                       {
                           new ServiceReplicaListener(
                               initParams =>
                               new ServiceRemotingListener<ITweet>(initParams, this))
                       };
        }

        protected override async Task RunAsync(CancellationToken token)
        {
            this.cancellationToken = token;
            Task.Factory.StartNew(this.CreateTweetMessages, this.cancellationToken);
            Task.Factory.StartNew(this.ConsumeTweetMessages, this.cancellationToken);
            this.cancellationToken.WaitHandle.WaitOne();
        }

        private void ConsumeTweetMessages()
        {
            var tweetQueue = this.StateManager.GetOrAddAsync<IReliableQueue<string>>("tweetQueue").Result;
            var scoreDictionary =
                this.StateManager.GetOrAddAsync<IReliableDictionary<string, decimal>>("scoreDictionary").Result;
            while (!this.cancellationToken.IsCancellationRequested)
            {
                using (var tx = this.StateManager.CreateTransaction())
                {
                    var message = tweetQueue.TryDequeueAsync(tx).Result;
                    if (message.HasValue)
                    {
                        var score = this.GetTweetSentiment(message.Value);
                        scoreDictionary.AddOrUpdateAsync(tx, message.Value, score, (key, value) => score);
                    }

                    tx.CommitAsync();
                }
            }
        }

        private void CreateTweetMessages()
        {
            while (!this.cancellationToken.IsCancellationRequested)
            {
                var topicQueue = this.StateManager.GetOrAddAsync<IReliableQueue<string>>("topicQueue").Result;
                using (var tx = this.StateManager.CreateTransaction())
                {
                    var topic = topicQueue.TryDequeueAsync(tx).Result;
                    if (topic.HasValue)
                    {
                        var tweets = this.GetTweetsForSubject(topic.Value);
                        var tweetQueue = this.StateManager.GetOrAddAsync<IReliableQueue<string>>("tweetQueue").Result;
                        foreach (var tweet in tweets)
                        {
                            tweetQueue.EnqueueAsync(tx, tweet).Wait();
                        }
                    }

                    tx.CommitAsync().Wait();
                }

                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }

        private decimal GetTweetSentiment(string message)
        {
            decimal score;
            var configurationPackage =
                this.ServiceInitializationParameters.CodePackageActivationContext.GetConfigurationPackageObject(
                    "Config");
            var amlaAccountKey =
                configurationPackage.Settings.Sections["UserSettings"].Parameters["AmlaAccountKey"].Value;
            var ServiceBaseUri = "https://api.datamarket.azure.com/";
            var accountKey = amlaAccountKey;
            using (var httpClient = new HttpClient())
            {
                var inputTextEncoded = Uri.EscapeUriString(message);
                httpClient.BaseAddress = new Uri(ServiceBaseUri);
                var creds = "AccountKey:" + accountKey;
                var authorizationHeader = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(creds));

                httpClient.DefaultRequestHeaders.Add("Authorization", authorizationHeader);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var sentimentRequest = "data.ashx/amla/text-analytics/v1/GetSentiment?Text=" + inputTextEncoded;
                var responseTask = httpClient.GetAsync(sentimentRequest);
                responseTask.Wait();
                var response = responseTask.Result;
                var contentTask = response.Content.ReadAsStringAsync();
                var content = contentTask.Result;
                if (!response.IsSuccessStatusCode)
                {
                    return -1;
                }

                dynamic sentimentResult = JsonConvert.DeserializeObject<dynamic>(content);
                score = (decimal)sentimentResult.Score;
            }

            return score;
        }

        private IEnumerable<string> GetTweetsForSubject(string topic)
        {
            var configurationPackage =
                this.ServiceInitializationParameters.CodePackageActivationContext.GetConfigurationPackageObject(
                    "Config");
            var accessToken = configurationPackage.Settings.Sections["UserSettings"].Parameters["AccessToken"].Value;
            var accessTokenSecret =
                configurationPackage.Settings.Sections["UserSettings"].Parameters["AccessTokenSecret"].Value;
            var consumerKey = configurationPackage.Settings.Sections["UserSettings"].Parameters["ConsumerKey"].Value;
            var consumerSecret =
                configurationPackage.Settings.Sections["UserSettings"].Parameters["ConsumerSecret"].Value;

            var authorizer = new SingleUserAuthorizer
            {
                CredentialStore =
                                         new SingleUserInMemoryCredentialStore
                                         {
                                             ConsumerKey =
                                                     consumerKey,
                                             ConsumerSecret =
                                                     consumerSecret,
                                             AccessToken =
                                                     accessToken,
                                             AccessTokenSecret
                                                     =
                                                     accessTokenSecret
                                         }
            };
            var twitterContext = new TwitterContext(authorizer);
            var searchResults = Enumerable.SingleOrDefault(
                (from search in twitterContext.Search
                 where search.Type == SearchType.Search && search.Query == topic && search.Count == 100
                 select search));
            if (searchResults != null && searchResults.Statuses.Count > 0)
            {
                return searchResults.Statuses.Select(status => status.Text);
            }

            return Enumerable.Empty<string>();
        }

        #endregion
    }
}