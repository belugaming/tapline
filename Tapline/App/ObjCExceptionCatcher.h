#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// Runs `block` and returns the Objective-C exception it raised, or nil.
/// Swift cannot catch these; use `ObjCException.catching` from Swift.
FOUNDATION_EXPORT NSException *_Nullable TaplineCatchObjCException(NS_NOESCAPE void (^block)(void));

NS_ASSUME_NONNULL_END
