using System;
using System.Threading.Tasks;
using Core;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Network
{
    public class NetworkManager : Singleton<NetworkManager>
    {
        public ISession CurrentSession;
        
        private QuerySessionsOptions _queryOptions; 
        
        [Header("Host Settings")]
        [SerializeField] public int maxPlayers = 4;
        [SerializeField] public bool isPrivate;
        [SerializeField] public string sessionName = "Unnamed";
        [SerializeField] public string password;
        
        private async void Start()
        {
            try
            {
                await UnityServices.InitializeAsync();
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"Sign in anonymously succeeded! PlayerID: {AuthenticationService.Instance.PlayerId}");
                _queryOptions.Count = 10;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        
        // SESSION JOINING //
        
        public async Task StartSessionAsHost()
        {
            var options = new SessionOptions
            {
                MaxPlayers = maxPlayers,
                IsPrivate = isPrivate,
                Name = sessionName,
                Password = password,
            }.WithRelayNetwork();
            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);
            
            Debug.Log($"Session {CurrentSession.Id} created! Join code: {CurrentSession.Code}");
        }

        public async Task JoinSessionByCode(string code)
        {
            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
        }
        
        public async Task JoinSessionById(string id)
        {
            CurrentSession = await MultiplayerService.Instance.JoinSessionByIdAsync(id);
        }
        
        // SESSION QUERIES //

        public async Task<QuerySessionsResults> QuerySessionsAsync()
        {
            return await MultiplayerService.Instance.QuerySessionsAsync(_queryOptions);
        }
        
        public void AddOrSetFilter(int propertyIndex, string value)
        {
            if (propertyIndex is < 0 or > 4)
            {
                Debug.LogError("Index out of range for available string properties (min: 0, max: 4)");
            }
            
            var toAdd = new FilterOption((FilterField)(5 + propertyIndex), value, 0);
            _queryOptions.FilterOptions.Add(toAdd);
        }
        
        public void AddOrSetFilter(int propertyIndex, int value, FilterOperation operation)
        {
            if (propertyIndex is < 0 or > 4)
            {
                Debug.LogError("Index out of range for available integer properties (min: 0, max: 4)");
            }
            
            var toAdd = new FilterOption((FilterField)(10 + propertyIndex), value.ToString(), operation);
            _queryOptions.FilterOptions.Add(toAdd);
        }
        
        public void RemoveFilter(int filterIndex)
        {
            _queryOptions.FilterOptions.RemoveAt(filterIndex);
        }

        public void ClearFilters()
        {
            _queryOptions.FilterOptions.Clear();
        }

        public void AddSortOption(bool isAscending, SortField sortField)
        {
            var toAdd = new SortOption(isAscending ? SortOrder.Ascending : SortOrder.Descending, sortField);
            _queryOptions.SortOptions.Add(toAdd);
        }

        public void RemoveSortOption(int optionIndex)
        {
            _queryOptions.SortOptions.RemoveAt(optionIndex);
        }

        public void ClearSortOptions()
        {
            _queryOptions.SortOptions.Clear();
        }
        
        // SESSION LEAVING //
        
        public async Task LeaveSession()
        {
            try
            {
                await CurrentSession.LeaveAsync();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
        
        // SESSION STATE //

        public void SetSessionIsLocked(bool isLocked = true)
        {
            if (CurrentSession is not { IsHost: true }) return; // Continue if session exists and host
            
            CurrentSession.AsHost().IsLocked = isLocked;
        }
    }
}
