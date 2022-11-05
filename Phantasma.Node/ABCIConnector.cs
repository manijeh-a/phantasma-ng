using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Phantasma.Business.Blockchain;
using Phantasma.Core.Cryptography;
using Phantasma.Core.Domain;
using Phantasma.Core.Numerics;
using Phantasma.Core.Types;
using Serilog;
using Tendermint;
using Tendermint.Abci;
using Tendermint.RPC;

namespace Phantasma.Node;
public class ABCIConnector : ABCIApplication.ABCIApplicationBase
{
    private Nexus _nexus;
    private PhantasmaKeys _owner;
    private NodeRpcClient _rpc;
    private IEnumerable<Address> _initialValidators;
    private SortedDictionary<int, Transaction>_systemTxs = new SortedDictionary<int, Transaction>();
    private List<Transaction> _broadcastedTxs = new List<Transaction>();

    // TODO add logger
    public ABCIConnector(IEnumerable<Address> initialValidators)
    {
        _initialValidators = initialValidators;
        Log.Information("ABCI Connector initialized");
    }

    public void SetNodeInfo(Nexus nexus, string tendermintEndpoint, PhantasmaKeys keys)
    {
        _owner = keys;
        _nexus = nexus;
        _rpc = new NodeRpcClient(tendermintEndpoint);
        _nexus.RootChain.ValidatorKeys = _owner;
    }

    public override Task<ResponseBeginBlock> BeginBlock(RequestBeginBlock request, ServerCallContext context)
    {
        Log.Information("Begin block {Height}", request.Header.Height);
        var response = new ResponseBeginBlock();
        try
        {
            var proposerAddress = Base16.Encode(request.Header.ProposerAddress.ToByteArray());
            Log.Information("proposer {ProposerAddress} current node {CurrentAddress}", proposerAddress, this._owner.Address.TendermintAddress);
            if (proposerAddress.Equals(this._owner.Address.TendermintAddress))
            {
                foreach (var tx in _systemTxs.OrderBy(x => x.Key))
                {
                    var txString = Base16.Encode(tx.Value.ToByteArray(true));
                    Log.Information("Broadcast tx {Transaction}", tx);
                    while (true)
                    {
                        try
                        {
                            _rpc.BroadcastTxSync(txString);
                            _broadcastedTxs.Add(tx.Value);
                            break;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                        }
                    }
                    Log.Information("Broadcast tx {Transaction} done", tx);
                }
            }
            _systemTxs.Clear();

            var chain = _nexus.RootChain as Chain;

            IEnumerable<Transaction> systemTransactions;
            systemTransactions = chain.BeginBlock(proposerAddress, request.Header.Height, this._initialValidators); 

            if (proposerAddress.Equals(this._owner.Address.TendermintAddress))
            {
                var idx = 0;
                foreach (var tx in systemTransactions)
                {
                    Log.Information("Broadcasting system transaction {Transaction}", tx);
                    _systemTxs.Add(idx, tx);
                    var txString = Base16.Encode(tx.ToByteArray(true));
                    Task.Factory.StartNew(() => _rpc.BroadcastTxSync(txString));
                    idx++;
                }
            }
            else
            {
                _systemTxs.Clear();
            }
        }
        catch (Exception e)
        {
            Log.Information(e.ToString());
        }
        
        return Task.FromResult(response);
    }
    
    public override Task<ResponseCheckTx> CheckTx(RequestCheckTx request, ServerCallContext context)
    {
        // TODO checktx 
        try
        {
            if (request.Type == CheckTxType.New)
            {
                var chain = _nexus.RootChain as Chain;
                (CodeType code, string message) = chain.CheckTx(request.Tx);

                var response = new ResponseCheckTx();
                response.Code = 0;
                if (code == CodeType.Ok)    
                {
                    return Task.FromResult(ResponseHelper.Check.Ok());
                }

                return Task.FromResult(ResponseHelper.Check.Create(code, message));
            }
        }
        catch (Exception e)
        {
            Log.Information("CheckTx failed: {Exception}", e);
        }

        return Task.FromResult(ResponseHelper.Check.Create(CodeType.Error, "Generic Error"));
    }
    
    public override Task<ResponseDeliverTx> DeliverTx(RequestDeliverTx request, ServerCallContext context)
    {
        var chain = _nexus.RootChain as Chain;

        var txString = request.Tx.ToStringUtf8();
        var newTx = Transaction.Unserialize(Base16.Decode(txString));

        var result = chain.DeliverTx(newTx);

        var bytes = Serialization.Serialize(result.Result);

        var response = new ResponseDeliverTx()
        {
            Code = result.Code,
            // Codespace cannot be null!
            Codespace = result.Codespace,
            Data = ByteString.CopyFrom(bytes),
        };

        if (result.Events.Count() > 0)
        {
            var newEvents = new List<Tendermint.Abci.Event>();
            foreach (var evt in result.Events)
            {
                var newEvent = new Tendermint.Abci.Event();
                var attributes = new EventAttribute[]
                {
                    // Value cannot be null!
                    new EventAttribute() { Key = "address", Value = evt.Address.ToString() },
                    new EventAttribute() { Key = "contract", Value = evt.Contract },
                    new EventAttribute() { Key = "data", Value = Base16.Encode(evt.Data) },
                };

                newEvent.Type = evt.Kind.ToString();
                newEvent.Attributes.AddRange(attributes);

                newEvents.Add(newEvent);
            }
            response.Events.AddRange(newEvents);
        }

        // check if a system tx was executed, if yes, remove it
        for (var i = 0; i < _broadcastedTxs.Count; i++)
        {
            var tx = _broadcastedTxs[i];
            if (tx.Hash == result.Hash)
            {
                Log.Information($"Transaction {tx.Hash} has been executed, remove now");
                _broadcastedTxs.Remove(tx);
            }
        }

        return Task.FromResult(response);
    }

    public override Task<ResponseEndBlock> EndBlock(RequestEndBlock request, ServerCallContext context)
    {
        Log.Information("End block {Height}", request.Height);
        var response = new ResponseEndBlock();
        try
        {
            var chain = _nexus.RootChain as Chain;
            var result = chain.EndBlock<ValidatorUpdate>();

            response.ValidatorUpdates.AddRange(result);

            // TODO
            //response.ConsensusParamUpdates = ???
            //response.Events = ???

            return Task.FromResult(response);
        }
        catch (Exception e)
        {
            Log.Information(e.ToString());
        }

        return Task.FromResult(response);
    }
    
    public override Task<ResponseCommit> Commit(RequestCommit request, ServerCallContext context)
    {
        var chain = _nexus.RootChain as Chain;
        var data = chain.Commit();
        var response = new ResponseCommit();
        //response.Data = ByteString.CopyFrom(data); // this would change the app hash, we don't want that
        return Task.FromResult(response);
    }


    public override Task<ResponseEcho> Echo(RequestEcho request, ServerCallContext context)
    {
        var echo = new ResponseEcho();
        echo.Message = request.Message;
        Log.Information("Echo " + echo.Message);
        return Task.FromResult(echo);
    }

    public override Task<ResponseFlush> Flush(RequestFlush request, ServerCallContext context)
    {
        Log.Information("RequestFlush has been called.");
        return Task.FromResult(new ResponseFlush());
    }

    public override Task<ResponseInfo> Info(RequestInfo request, ServerCallContext context)
    {
        Hash lastBlockHash;
        Block lastBlock = null;
        try 
        {
            lastBlockHash = _nexus.RootChain.GetLastBlockHash();
            lastBlock = _nexus.RootChain.GetBlockByHash(lastBlockHash);
            var version = _nexus.GetProtocolVersion(_nexus.RootStorage);
        }
        catch (Exception e)
        {
            Log.Information("Error getting info {Exception}", e);
        }

        ResponseInfo response = new ResponseInfo()
        {
            AppVersion = 0,
            LastBlockHeight = (lastBlock != null) ? (long)lastBlock.Height : 0,
            Version = "0.0.1",
        };

        return Task.FromResult(response);
    }

    public override Task<ResponseInitChain> InitChain(RequestInitChain request, ServerCallContext context)
    {
        var response = new ResponseInitChain();
        var timestamp = new Timestamp((uint) request.Time.Seconds);

        try
        {
            Dictionary<int, Transaction> systemTransactions;
            systemTransactions = _nexus.CreateGenesisBlock(timestamp, 0, this._owner, this._initialValidators);

            var idx = 0;
            foreach (var tx in systemTransactions.OrderByDescending(x => x.Key))
            {
                Log.Information("Preparing tx {Transaction} for broadcast", tx.Value);
                _systemTxs.Add(tx.Key, tx.Value);
                idx++;
            }
        }
        catch (Exception e)
        {
            Log.Information(e.ToString());
        }

        var appHash = Encoding.UTF8.GetBytes("A Phantasma was born...");
        response.AppHash = ByteString.CopyFrom(appHash);
        return Task.FromResult( response );
    }

    public override Task<ResponseQuery> Query(RequestQuery request, ServerCallContext context)
    {
        return Task.FromResult( new ResponseQuery());
    }

    public override Task<ResponseListSnapshots> ListSnapshots(RequestListSnapshots request, ServerCallContext context)
    {
        return Task.FromResult( new ResponseListSnapshots());
    }

    public override Task<ResponseOfferSnapshot> OfferSnapshot(RequestOfferSnapshot request, ServerCallContext context)
    {
        return Task.FromResult( new ResponseOfferSnapshot());
    }

    public override Task<ResponseLoadSnapshotChunk> LoadSnapshotChunk(RequestLoadSnapshotChunk request, ServerCallContext context)
    {
        return Task.FromResult( new ResponseLoadSnapshotChunk());
    }

    public override Task<ResponseApplySnapshotChunk> ApplySnapshotChunk(RequestApplySnapshotChunk request, ServerCallContext context)
    {
        return Task.FromResult( new ResponseApplySnapshotChunk());
    }
}
